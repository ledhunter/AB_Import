#!/usr/bin/env node
/*
 Генератор PostgreSQL DDL для visary_db из schema_export.csv.

 Алгоритм:
   1. Читаем CSV → группируем по таблицам (schema.name).
   2. Загружаем seed-список из visary-seed-tables.txt.
   3. Транзитивно расширяем seed по FK: если таблица A в наборе и у неё FK на B → добавляем B.
      Повторяем, пока набор не стабилизируется.
   4. Для каждой включённой таблицы генерируем CREATE TABLE с колонками, PK.
   5. Отдельным блоком — все FK (ALTER TABLE ... ADD CONSTRAINT) после CREATE TABLE.
   6. Имена таблиц/колонок в "double quotes" (PostgreSQL case-sensitive + зарезервированные слова: User, Right).
   7. Целевая таблица FK угадывается по имени constraint: FK_<src>_<target>_<col>.

 Запуск:
   node scripts/generate-visary-ddl.mjs

 На выходе:
   db/visary/init/01-schema.sql
   build/visary-ddl-report.json (отчёт о включённых таблицах, FK, предупреждениях)
*/

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const ROOT = resolve(__dirname, '..');

const CSV_PATH = resolve(ROOT, 'Context', 'schema_export.csv');
const SEED_PATH = resolve(ROOT, 'scripts', 'visary-seed-tables.txt');
const OUT_SQL = resolve(ROOT, 'db', 'visary', 'init', '01-schema.sql');
const OUT_REPORT = resolve(ROOT, 'build', 'visary-ddl-report.json');

// ───────────────────── CSV parser (минимальный, без зависимостей) ─────────────────────
function parseCsv(text) {
    const lines = text.split(/\r?\n/);
    const header = splitCsvLine(lines[0]);
    const rows = [];
    for (let i = 1; i < lines.length; i++) {
        const line = lines[i];
        if (!line) continue;
        const cells = splitCsvLine(line);
        const row = {};
        for (let j = 0; j < header.length; j++) row[header[j]] = cells[j] ?? '';
        rows.push(row);
    }
    return rows;
}
function splitCsvLine(line) {
    const out = [];
    let cur = '';
    let inQ = false;
    for (let i = 0; i < line.length; i++) {
        const ch = line[i];
        if (inQ) {
            if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++; }
            else if (ch === '"') inQ = false;
            else cur += ch;
        } else {
            if (ch === ',') { out.push(cur); cur = ''; }
            else if (ch === '"') inQ = true;
            else cur += ch;
        }
    }
    out.push(cur);
    return out;
}

// ───────────────────── Mapping PostgreSQL types ─────────────────────
function pgType(t) {
    // information_schema даёт условные имена: int4, float8, _text. Конвертируем в человеческие PostgreSQL типы.
    const map = {
        int4: 'integer',
        int8: 'bigint',
        int2: 'smallint',
        float4: 'real',
        float8: 'double precision',
        bool: 'boolean',
        text: 'text',
        date: 'date',
        timestamp: 'timestamp',
        timestamptz: 'timestamp with time zone',
        timetz: 'time with time zone',
        uuid: 'uuid',
        bytea: 'bytea',
        numeric: 'numeric',
        jsonb: 'jsonb',
        json: 'json',
        _text: 'text[]',
        _int4: 'integer[]',
        _int8: 'bigint[]',
    };
    if (map[t]) return map[t];
    if (t.startsWith('varchar(')) return `character varying${t.slice('varchar'.length)}`;
    if (t.startsWith('char(')) return `character${t.slice('char'.length)}`;
    if (t.startsWith('numeric(')) return t;
    return t; // fallback — оставляем как есть
}

// ───────────────────── Identifier quoting ─────────────────────
function q(name) {
    return `"${name.replace(/"/g, '""')}"`;
}

// ───────────────────── Read seed ─────────────────────
function readSeed() {
    const txt = readFileSync(SEED_PATH, 'utf8');
    const set = new Set();
    for (const line of txt.split(/\r?\n/)) {
        const t = line.trim();
        if (!t || t.startsWith('#')) continue;
        if (!t.includes('.')) continue;
        set.add(t);
    }
    return set;
}

// ───────────────────── Detect FK target table ─────────────────────
/**
 * Имя FK по EF Core-конвенции: FK_<src_table>_<target_table>_<column>
 * Срабатывает в подавляющем большинстве случаев.
 * Если не получилось — возвращаем null + warning.
 *
 * tableSet — все таблицы в текущей схеме (Set имён без схемы), нужен для разрешения,
 *   когда target состоит из нескольких частей (например `Role_Permissions`).
 */
function inferFkTarget(constraintName, srcTable, columnName, allTablesByName) {
    if (!constraintName.startsWith('FK_')) return null;
    let body = constraintName.substring(3); // убираем "FK_"
    const srcPrefix = srcTable + '_';
    if (!body.startsWith(srcPrefix)) {
        // EF иногда сокращает префикс источника или обрезает имя длинной FK (~);
        // тогда middle угадать нельзя — пробуем по суффиксу.
        body = body.replace(/~$/, '');
    } else {
        body = body.substring(srcPrefix.length);
    }
    // обрезаем суффикс _<column>
    const colSuffix1 = '_' + columnName;
    const colSuffix2 = '_' + columnName + 'Id';
    const colSuffix3 = '_' + columnName.replace(/Id$/i, '');
    if (body.endsWith(colSuffix1)) body = body.slice(0, -colSuffix1.length);
    else if (body.endsWith(colSuffix2)) body = body.slice(0, -colSuffix2.length);
    // body теперь = candidate target
    if (allTablesByName.has(body)) return body;
    // fallback — попробуем найти по самому длинному совпадению
    const candidates = [...allTablesByName].filter((t) => body.includes(t));
    if (candidates.length === 0) return null;
    candidates.sort((a, b) => b.length - a.length);
    return candidates[0];
}

// ───────────────────── Main ─────────────────────
function main() {
    console.log('[generate-visary-ddl] чтение CSV…');
    const rows = parseCsv(readFileSync(CSV_PATH, 'utf8'));
    console.log(`[generate-visary-ddl] строк колонок: ${rows.length}`);

    // Группируем по таблицам.
    const tables = new Map(); // key "schema.name" → {schema,name,columns:[],pk:[],fks:[]}
    for (const r of rows) {
        const key = `${r.table_schema}.${r.table_name}`;
        let t = tables.get(key);
        if (!t) {
            t = {
                schema: r.table_schema,
                name: r.table_name,
                columns: [],
                pk: [],
                fks: [],
            };
            tables.set(key, t);
        }
        const colName = r.column_name;
        if (t.columns.find((c) => c.name === colName) === undefined) {
            t.columns.push({
                name: colName,
                type: r.data_type,
                nullable: r.is_nullable === 'YES',
                default: r.column_default || null,
                position: parseInt(r.column_position || '0', 10),
            });
        }
        if (r.constraint_type === 'PRIMARY KEY') {
            if (!t.pk.includes(colName)) t.pk.push(colName);
        }
        if (r.constraint_type === 'FOREIGN KEY') {
            t.fks.push({ column: colName, constraintName: r.constraint_name });
        }
    }
    console.log(`[generate-visary-ddl] таблиц всего: ${tables.size}`);

    // Стабильная сортировка колонок по position.
    for (const t of tables.values()) t.columns.sort((a, b) => a.position - b.position);

    // Карта: имя таблицы (без схемы) → список full keys (для разрешения FK).
    const tableNameIndex = new Map(); // name → [fullKey, ...]
    for (const t of tables.values()) {
        if (!tableNameIndex.has(t.name)) tableNameIndex.set(t.name, []);
        tableNameIndex.get(t.name).push(`${t.schema}.${t.name}`);
    }
    const allTableNames = new Set(tableNameIndex.keys());

    // Транзитивное расширение seed по FK.
    const seed = readSeed();
    console.log(`[generate-visary-ddl] seed: ${seed.size} таблиц`);
    const included = new Set(seed);
    const warnings = [];
    let changed = true;
    while (changed) {
        changed = false;
        for (const fullKey of [...included]) {
            const t = tables.get(fullKey);
            if (!t) {
                warnings.push(`seed-таблица не найдена в CSV: ${fullKey}`);
                included.delete(fullKey);
                continue;
            }
            for (const fk of t.fks) {
                const targetName = inferFkTarget(fk.constraintName, t.name, fk.column, allTableNames);
                if (!targetName) {
                    warnings.push(`не удалось определить target для FK ${fk.constraintName} (${fullKey}.${fk.column})`);
                    continue;
                }
                const candidates = tableNameIndex.get(targetName) || [];
                // Выбираем кандидата в той же схеме приоритетно, иначе любой.
                let targetKey = candidates.find((k) => k.startsWith(t.schema + '.')) || candidates[0];
                if (!targetKey) continue;
                fk.targetTable = targetName;
                fk.targetSchema = targetKey.split('.')[0];
                if (!included.has(targetKey)) {
                    included.add(targetKey);
                    changed = true;
                }
            }
        }
    }
    console.log(`[generate-visary-ddl] после транзитивного расширения: ${included.size} таблиц`);

    // Для уже включённых таблиц зафиксируем target всех FK (некоторые могли быть пропущены из-за порядка).
    for (const fullKey of included) {
        const t = tables.get(fullKey);
        if (!t) continue;
        for (const fk of t.fks) {
            if (fk.targetTable) continue;
            const targetName = inferFkTarget(fk.constraintName, t.name, fk.column, allTableNames);
            if (!targetName) continue;
            const candidates = tableNameIndex.get(targetName) || [];
            const targetKey = candidates.find((k) => k.startsWith(t.schema + '.')) || candidates[0];
            if (!targetKey) continue;
            fk.targetTable = targetName;
            fk.targetSchema = targetKey.split('.')[0];
        }
    }

    // Группируем включённые таблицы по схеме.
    const bySchema = new Map();
    for (const fullKey of included) {
        const t = tables.get(fullKey);
        if (!t) continue;
        if (!bySchema.has(t.schema)) bySchema.set(t.schema, []);
        bySchema.get(t.schema).push(t);
    }
    for (const arr of bySchema.values()) arr.sort((a, b) => a.name.localeCompare(b.name));

    // ───── Генерация SQL ─────
    const sql = [];
    sql.push('-- Авто-сгенерировано scripts/generate-visary-ddl.mjs из Context/schema_export.csv');
    sql.push(`-- Дата: ${new Date().toISOString()}`);
    sql.push(`-- Включено таблиц: ${included.size} (из ${tables.size})`);
    sql.push('');
    sql.push('SET client_encoding = \'UTF8\';');
    sql.push('SET standard_conforming_strings = on;');
    sql.push('SET check_function_bodies = false;');
    sql.push('SET client_min_messages = warning;');
    sql.push('');

    // Схемы
    for (const sch of [...bySchema.keys()].sort()) {
        sql.push(`CREATE SCHEMA IF NOT EXISTS ${q(sch)};`);
    }
    sql.push('');

    // CREATE TABLE
    let totalFks = 0;
    for (const sch of [...bySchema.keys()].sort()) {
        sql.push(`-- ═══════════════════════ Schema: ${sch} ═══════════════════════`);
        for (const t of bySchema.get(sch)) {
            sql.push('');
            sql.push(`CREATE TABLE IF NOT EXISTS ${q(t.schema)}.${q(t.name)} (`);
            const colLines = t.columns.map((c) => {
                let line = `    ${q(c.name)} ${pgType(c.type)}`;
                // PK int4 → IDENTITY (только если одна колонка в PK и это int4 NOT NULL)
                const isSinglePk = t.pk.length === 1 && t.pk[0] === c.name;
                if (isSinglePk && c.type === 'int4' && !c.nullable) {
                    line += ' GENERATED BY DEFAULT AS IDENTITY';
                }
                if (!c.nullable) line += ' NOT NULL';
                if (c.default) line += ` DEFAULT ${c.default}`;
                return line;
            });
            if (t.pk.length > 0) {
                colLines.push(`    CONSTRAINT ${q('PK_' + t.name)} PRIMARY KEY (${t.pk.map(q).join(', ')})`);
            }
            sql.push(colLines.join(',\n'));
            sql.push(');');
        }
        sql.push('');
    }

    // ALTER TABLE ... ADD FOREIGN KEY (отложенно, после всех CREATE TABLE)
    sql.push('-- ═══════════════════════ Foreign keys ═══════════════════════');
    sql.push('');
    for (const sch of [...bySchema.keys()].sort()) {
        for (const t of bySchema.get(sch)) {
            for (const fk of t.fks) {
                if (!fk.targetTable) continue;
                // Включаем только FK на таблицы в нашем срезе.
                const targetKey = `${fk.targetSchema}.${fk.targetTable}`;
                if (!included.has(targetKey)) continue;
                totalFks++;
                const cn = q(fk.constraintName.replace(/~$/, '')); // если был обрезан — убираем тильду
                sql.push(
                    `ALTER TABLE ${q(t.schema)}.${q(t.name)} ` +
                    `ADD CONSTRAINT ${cn} FOREIGN KEY (${q(fk.column)}) ` +
                    `REFERENCES ${q(fk.targetSchema)}.${q(fk.targetTable)} ("ID") ` +
                    `ON DELETE NO ACTION ON UPDATE NO ACTION;`
                );
            }
        }
    }
    sql.push('');

    // Запись результатов
    mkdirSync(dirname(OUT_SQL), { recursive: true });
    writeFileSync(OUT_SQL, sql.join('\n'), 'utf8');
    mkdirSync(dirname(OUT_REPORT), { recursive: true });
    writeFileSync(
        OUT_REPORT,
        JSON.stringify(
            {
                generatedAt: new Date().toISOString(),
                csvRows: rows.length,
                totalTablesInCsv: tables.size,
                seedTables: [...seed],
                includedTablesCount: included.size,
                includedTables: [...included].sort(),
                tablesPerSchema: Object.fromEntries(
                    [...bySchema.entries()].map(([s, arr]) => [s, arr.map((t) => t.name).sort()])
                ),
                totalForeignKeys: totalFks,
                warnings,
            },
            null,
            2
        ),
        'utf8'
    );

    console.log(`[generate-visary-ddl] ✓ SQL: ${OUT_SQL}`);
    console.log(`[generate-visary-ddl] ✓ отчёт: ${OUT_REPORT}`);
    console.log(`[generate-visary-ddl] таблиц включено: ${included.size}, FK: ${totalFks}, warnings: ${warnings.length}`);
    if (warnings.length > 0) {
        console.log('--- Первые 10 предупреждений ---');
        warnings.slice(0, 10).forEach((w) => console.log('  ! ' + w));
    }
}

main();
