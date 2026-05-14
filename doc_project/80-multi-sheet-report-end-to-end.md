# 🗂️ Многолистовой отчёт импорта — `Sheet` от БД до UI

## 📋 Описание

Дополнение к [72-multi-sheet-import.md](72-multi-sheet-import.md): многолистовой
импорт уже хранит `Sheet` в `staged_rows`/`import_errors` (миграция
`SessionId, Sheet, SourceRowNumber`), но `GET /api/imports/{id}/report` и
UI-маппер раньше теряли это поле. В итоге:

- одинаковые `SourceRowNumber` с разных листов «склеивались» — ошибки одной
  строки прилипали к одноимённой строке другого листа;
- сортировка отчёта была чисто по `SourceRowNumber` — строки разных листов
  перемешивались;
- React-`key={rowNumber}` в `SessionRowsTable` давал warning «duplicate keys»
  и непредсказуемый ре-рендер.

Доработка проводит `Sheet` сквозным каналом: **БД → API DTO → UiReport → UI**,
сохраняя композитный ключ `(Sheet, SourceRowNumber)` всюду.

---

## ✅ Правильная реализация

### 1. Backend — `ImportsController.GetReport`

```csharp
// KiloImportService.Api/Controllers/ImportsController.cs (around line 258)

var rows = await rowsQ
    .OrderBy(r => r.Sheet).ThenBy(r => r.SourceRowNumber)   // 👈 сначала лист
    .Skip(skip).Take(take)
    .Select(r => new { r.SourceRowNumber, r.Sheet, status = r.Status.ToString() })
    .ToListAsync(ct);

var errors = await _db.Errors.AsNoTracking()
    .Where(e => e.ImportSessionId == id)
    .OrderBy(e => e.Sheet).ThenBy(e => e.SourceRowNumber)   // 👈 та же сортировка
    .Select(e => new { e.SourceRowNumber, e.Sheet, e.ColumnName, e.ErrorCode, e.Message })
    .ToListAsync(ct);
```

### ⚠️ Важно

- Поле `Sheet` — `string?` (null для одностраничных импортов типа FinModel,
  не null для rooms/budget multi-sheet).
- Сортировка `OrderBy(Sheet).ThenBy(SourceRowNumber)` для **строк И ошибок**
  одинаковая — фронт ожидает совпадающий порядок при простом zip-merge
  (см. `toUiReport`).
- DTO остаются анонимными (без выделенного `record`) — это согласовано с
  остальными методами контроллера и упрощает добавление полей.
- В `staged_rows` `Sheet` — часть составного уникального индекса
  `(SessionId, Sheet, SourceRowNumber)` (см. миграция в
  [72-multi-sheet-import.md](72-multi-sheet-import.md)). Без `Sheet` в `Select`
  фронт не смог бы развести коллизии.

### 2. Frontend — типы API/UI

```ts
// KiloImportService.Web/src/types/api.ts
export interface ApiImportRow {
  sourceRowNumber: number;
  sheet: string | null;          // 👈 добавлено
  status: ApiStagedRowStatus;
}

export interface ApiImportError {
  sourceRowNumber: number;
  sheet: string | null;          // 👈 добавлено
  columnName: string | null;
  errorCode: string;
  message: string;
}

// KiloImportService.Web/src/types/session.ts
export interface UiRowError {
  rowNumber: number;             // 0 — file-level
  sheet: string | null;          // null — file-level или одностраничный импорт
  columnName: string | null;
  errorCode: string;
  message: string;
}

export interface UiReportRow {
  rowNumber: number;
  sheet: string | null;          // «Квартиры», «Машиноместа», … или null
  status: RowStatus;
  errors: UiRowError[];
}
```

### 3. Frontend — композитный ключ в `toUiReport`

```ts
// KiloImportService.Web/src/services/importMappers.ts

// Ошибки группируем по (Sheet, RowNumber): уникальность строки в многолистовом
// импорте определяется именно этой парой (см. doc_project/72-multi-sheet-import.md).
const rowKey = (sheet: string | null | undefined, rowNumber: number): string =>
  `${sheet ?? ''}::${rowNumber}`;

const errorsByRow = new Map<string, UiRowError[]>();
for (const apiErr of api.errors ?? []) {
  const ui = toUiRowError(apiErr);
  if (ui.rowNumber <= 0) { fileLevelErrors.push(ui); continue; }
  const key = rowKey(ui.sheet, ui.rowNumber);
  const list = errorsByRow.get(key);
  if (list) list.push(ui); else errorsByRow.set(key, [ui]);
}

const rows: UiReportRow[] = (api.rows ?? []).map((r) => ({
  rowNumber: r.sourceRowNumber,
  sheet: r.sheet,
  status: r.status,
  errors: errorsByRow.get(rowKey(r.sheet, r.sourceRowNumber)) ?? [],
}));

// Осиротевшие ошибки — пар (sheet, rowNumber), которых нет в rows.
const seenKeys = new Set(rows.map((r) => rowKey(r.sheet, r.rowNumber)));
for (const [key, errors] of errorsByRow.entries()) {
  if (!seenKeys.has(key) && errors.length > 0) {
    rows.push({
      rowNumber: errors[0].rowNumber,
      sheet: errors[0].sheet,
      status: 'Invalid',
      errors,
    });
  }
}

rows.sort((a, b) => {
  const sa = a.sheet ?? '';
  const sb = b.sheet ?? '';
  if (sa !== sb) return sa.localeCompare(sb, 'ru');   // 👈 кириллица отсортирована правильно
  return a.rowNumber - b.rowNumber;
});
```

### ⚠️ Важно

- `rowKey` использует разделитель `::`, который не может встретиться в имени
  листа XLSX (Excel запрещает `:` в `worksheet.Name`). Делает ключ безопасным
  без эскейпинга.
- `sheet ?? ''` в ключе — это **важно**: в FinModel/одностраничных импортах
  все строки имеют `sheet === null`, и ключ должен быть консистентным
  (`'::42'`), иначе осиротевшие ошибки никогда не сматчатся.
- Сортировка `localeCompare(_, 'ru')` — без локали кириллические листы
  («Квартиры», «Машиноместа», «Кладовые») идут в порядке code-point, что
  визуально хаотично. С `'ru'` — алфавитно.
- Стабильность порядка: бэк уже отсортировал — `rows.sort` нужен только для
  «дописанных снизу» осиротевших ошибок.

### 4. UI — группировка по листам с заголовками

```tsx
// KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx

const grouped = useMemo(() => {
  const groups: { sheet: string | null; rows: UiReportRow[] }[] = [];
  const indexBySheet = new Map<string, number>();
  for (const r of filtered) {
    const key = r.sheet ?? '';
    const idx = indexBySheet.get(key);
    if (idx === undefined) {
      indexBySheet.set(key, groups.length);
      groups.push({ sheet: r.sheet, rows: [r] });
    } else {
      groups[idx].rows.push(r);
    }
  }
  return groups;
}, [filtered]);

// Заголовок листа показываем, если хотя бы один лист имеет имя
// или групп больше одной.
const showSheetHeaders =
  grouped.some((g) => g.sheet && g.sheet.length > 0) || grouped.length > 1;

// В JSX — отдельный <tbody> на каждую группу:
grouped.map((group, gi) => (
  <tbody key={group.sheet ?? `__nosheet__${gi}`}>
    {showSheetHeaders && (
      <tr className="sheet-header-row">
        <td colSpan={3}>
          <span className="sheet-header-row__title">
            Лист: {group.sheet || '— без листа —'}
          </span>
          <span className="sheet-header-row__count">
            {group.rows.length} стр.
          </span>
        </td>
      </tr>
    )}
    {group.rows.map((row) => (
      <tr key={`${row.sheet ?? ''}::${row.rowNumber}`} ...>
        ...
      </tr>
    ))}
  </tbody>
))
```

```css
/* App.css */
.sheet-header-row > td {
  background: #f3f4f6;
  border-top: 1px solid #d8d8d8;
  border-bottom: 1px solid #d8d8d8;
  padding: 8px 12px;
  font-size: 13px;
  color: #374151;
}
.sheet-header-row__title { font-weight: 600; margin-right: 12px; }
.sheet-header-row__count { color: #6b7280; font-size: 12px; }
```

### ⚠️ Важно

- На каждый лист — **свой `<tbody>`**. Это валидный HTML (`<table>` допускает
  несколько `<tbody>`) и даёт чёткий «разделитель» между группами без хаков.
- `key` строки — **составной** `` `${row.sheet ?? ''}::${row.rowNumber}` ``.
  Иначе при одинаковом `rowNumber=1` на «Квартиры» и «Машиноместа» React
  выдаст warning «Encountered two children with the same key».
- `showSheetHeaders` отключает заголовки для одностраничных импортов
  (FinModel, budget), где `sheet === null` у всех строк — таблица выглядит
  как раньше.
- Текст заголовка `Лист: {name}` + счётчик строк выровнены в одной строке —
  не загромождает интерфейс.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — Select без Sheet
.Select(r => new { r.SourceRowNumber, status = r.Status.ToString() })
// → фронт получает «голый» rowNumber, дубли с разных листов перемешиваются
```

```csharp
// НЕПРАВИЛЬНО — OrderBy(SourceRowNumber) без ThenBy(Sheet)
.OrderBy(r => r.SourceRowNumber)
// → лист «Машиноместа» строки 1-5 будут чередоваться с «Квартиры» строки 1-5
```

```ts
// НЕПРАВИЛЬНО — ключ только по rowNumber
const errorsByRow = new Map<number, UiRowError[]>();
errorsByRow.set(ui.rowNumber, ...)
// → ошибки квартиры №1 склеятся с ошибками машиноместа №1
```

```ts
// НЕПРАВИЛЬНО — забыть null-fallback
const key = `${sheet}::${rowNumber}`;            // sheet=null → "null::42"
// → ключ будет литералом «null», а не пустой строкой; путаница с одностраничными импортами
```

```ts
// НЕПРАВИЛЬНО — localeCompare без локали
rows.sort((a, b) => (a.sheet ?? '').localeCompare(b.sheet ?? ''));
// → в среде с en-US «Кладовые» окажутся в конце; нужен явный 'ru'
```

---

## 📍 Применение в проекте

| Слой | Файл | Поле/ключ |
|------|------|-----------|
| API DTO (rows) | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) `GetReport` | `Select(r => new { r.SourceRowNumber, r.Sheet, ... })` |
| API DTO (errors) | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) `GetReport` | `Select(e => new { ..., e.Sheet, ... })` |
| API типы | [types/api.ts](../KiloImportService.Web/src/types/api.ts) | `ApiImportRow.sheet`, `ApiImportError.sheet` |
| UI типы | [types/session.ts](../KiloImportService.Web/src/types/session.ts) | `UiReportRow.sheet`, `UiRowError.sheet` |
| Маппинг | [services/importMappers.ts](../KiloImportService.Web/src/services/importMappers.ts) | `rowKey()`, `toUiReport`, ru-сортировка |
| UI рендер групп | [components/ImportSession/SessionRowsTable.tsx](../KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx) | `grouped`, `showSheetHeaders`, `<tbody>` per sheet, key `${sheet}::${rowNumber}` |
| Стили заголовка листа | [App.css](../KiloImportService.Web/src/App.css) | `.sheet-header-row`, `.sheet-header-row__title/__count` |

---

## 🚀 Деплой / Пересборка

Эта фича изменяет **API-контракт** (новое поле `sheet` в ответе
`GET /api/imports/{id}/report`) **И** UI одновременно. Пересобирать **оба**
образа:

```bash
docker compose build backend frontend
docker compose up -d
```

### ⚠️ Типичный промах

Пересобрать только один образ → второй крутит старый код. Симптом:

| Что пересобрано | Симптом |
|----------------|---------|
| Только `frontend` | API возвращает `rows` без поля `sheet` → `showSheetHeaders === false` → заголовки групп не появляются, строки идут плоским списком с дубликатами `№` |
| Только `backend`  | API возвращает `sheet`, но в UI старый тип `UiReportRow` без поля — `row.sheet === undefined`, группировка не работает |

Проверка контракта после билда:

```bash
curl -s "http://localhost:5000/api/imports/<sessionId>/report?take=3" | jq '.rows[0]'
# должно содержать: { "sourceRowNumber": 2, "sheet": "Квартиры", "status": "Applied" }
```

После рестарта браузер — **Ctrl+F5** (SPA bundle кэшируется).

---

## 🔗 Связанная документация

- [72-multi-sheet-import.md](72-multi-sheet-import.md) — 5 мест согласования
  многолистового импорта (БД-индекс, парсер, пайплайн, маппер, UI). Этот файл
  расширяет «UI»-пункт.
- [73-import-history-page.md](73-import-history-page.md) — переиспользование
  `SessionRowsTable` в read-only «История импортов»; та же модель отчёта.
- [79-rooms-import-validation-and-fileupload-ux.md](79-rooms-import-validation-and-fileupload-ux.md)
  — `required_missing` для `RoomsCount` в квартирах. Ошибка приходит с
  `Sheet="Квартиры"` и теперь корректно матчится со строкой.

---

## 🎯 Чек-лист

- [ ] `GET /api/imports/{id}/report` возвращает `sheet` для каждой строки и каждой ошибки
- [ ] Строки отсортированы по `(Sheet, SourceRowNumber)` — листы не перемешиваются
- [ ] При двух листах с одинаковым `SourceRowNumber` ошибки прилипают к правильной строке
- [ ] Кириллические имена листов сортируются алфавитно (`'ru'` локаль)
- [ ] Осиротевшие ошибки (без соответствующей строки) попадают в `rows` с правильным `sheet`
- [ ] `SessionRowsTable` рендерит каждый лист в отдельном `<tbody>` с серым заголовком «Лист: …»
- [ ] Для одностраничного импорта (FinModel) заголовки не показываются (`showSheetHeaders = false`)
- [ ] Ключ строки — составной `${sheet}::${rowNumber}`; нет React-warning о дубликатах
