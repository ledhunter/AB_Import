// Постинсталл-патч для пакетов @alfalab/core-components-*.
//
// Пакеты импортируют сиблингов через подпуть `/esm` (директория, не файл),
// но в их `package.json` нет поля `exports`. Node-резолвер раскрывает это
// в `esm/index.js` по дефолту, а Vite 8 / Rolldown — нет, и сборка падает с
//   `Rolldown failed to resolve import "@alfalab/core-components-*/esm"`.
//
// История:
//   v1.0 — клали `<pkg>/esm/package.json:{main:./index.js}` (sub-package трюк).
//          Работало на Windows-локали, НЕ работало в Docker Alpine
//          (резолверы разных платформ Rolldown по-разному трактуют sub-package).
//   v2.0 — клали `<pkg>/esm.js` со `export * from './esm/index.js'`. Не работало
//          ни в Docker, ни локально — Rolldown при subpath-резолве в подпакете
//          без `exports` лезет в подпапку, файл-shim в корне игнорирует.
//   v3.0 (актуально) — добавляем поле `exports` прямо в основной `package.json`
//          пакета. Это Node.js Package Exports — стандарт, Rolldown его уважает
//          одинаково на всех платформах. `"./*": "./*"` — pass-through, сохраняет
//          доступ ко всем подпутям (стили, cssm, …), которые могли быть и до фикса.
//
// См. doc_project/134-postinstall-patch-alfalab-esm-package-json.md.

import {
  readFileSync,
  writeFileSync,
  existsSync,
  statSync,
  unlinkSync,
  readdirSync,
} from 'node:fs';
import { join } from 'node:path';

const root = join(process.cwd(), 'node_modules', '@alfalab');
if (!existsSync(root)) {
  console.log('[patch-alfalab-esm] node_modules/@alfalab отсутствует — пропуск');
  process.exit(0);
}

const norm = (p) => `./${String(p).replace(/^\.?\/*/, '')}`;

let patched = 0;
let skipped = 0;
let cleaned = 0;

for (const name of readdirSync(root)) {
  // Метапакет `@alfalab/core-components` (без суффикса) использует сложные
  // подпути `<pkg>/select/desktop`, `<pkg>/button/Component`, etc. У него нет
  // импорта `/esm`-подпути сиблингов — только у подкомпонент-пакетов
  // `core-components-X`. Если добавить ему `exports` с pass-through `"./*": "./*"`,
  // строгий резолв сломает доступ к `<pkg>/select/desktop`-вложенным index.js
  // (Node.js Package Exports не делает auto-index resolution). Патчим строго
  // подкомпоненты `core-components-X` — у них один значимый подпуть `/esm`.
  if (!name.startsWith('core-components-')) continue;

  const pkgRoot = join(root, name);
  if (!statSync(pkgRoot).isDirectory()) continue;
  const esmDir = join(pkgRoot, 'esm');
  if (!existsSync(esmDir) || !statSync(esmDir).isDirectory()) continue;
  if (!existsSync(join(esmDir, 'index.js'))) continue;

  // Очистка артефактов прошлых стратегий (v1.0 sub-package.json, v2.0 esm.js).
  // Безопасно: они либо отсутствуют, либо игнорируются Rolldown'ом в пользу
  // нового exports-поля. Удаляем для чистоты при повторных npm install.
  for (const old of [join(esmDir, 'package.json'), join(pkgRoot, 'esm.js')]) {
    if (existsSync(old)) {
      unlinkSync(old);
      cleaned++;
    }
  }

  const pkgFile = join(pkgRoot, 'package.json');
  let pkg;
  try {
    pkg = JSON.parse(readFileSync(pkgFile, 'utf8'));
  } catch {
    continue;
  }

  // Если у пакета уже есть exports — НЕ трогаем (значит, мейнтейнеры подняли
  // версию и сами добавили subpath-экспорты; наш патч становится не нужен).
  if (pkg.exports !== undefined) {
    skipped++;
    continue;
  }

  const mainPath = norm(pkg.main || 'index.js');
  const modulePath = norm(pkg.module || 'esm/index.js');

  // Node.js Package Exports — строгий резолв БЕЗ auto-index. Если импорт идёт
  // на подпапку (например `<pkg>/desktop`), нужно явно объявить
  // `./desktop`: `./desktop/index.js`, иначе резолвер вернёт путь к директории
  // и не найдёт entry. У `@alfalab/core-components-select` есть подпапки
  // `desktop/`, `mobile/`, `responsive/`, …, каждая с index.js — все они
  // должны быть в exports. Пробегаем по корневым подпапкам с `index.js` и
  // регистрируем их явно. `./*`: `./*` — pass-through для оставшихся файлов
  // (CSS, .json и т.д.).
  const subdirs = readdirSync(pkgRoot).filter((d) => {
    const p = join(pkgRoot, d);
    try {
      return statSync(p).isDirectory() && existsSync(join(p, 'index.js'));
    } catch {
      return false;
    }
  });

  const exports = {
    '.': {
      import: modulePath,
      require: mainPath,
      default: mainPath,
    },
    './package.json': './package.json',
  };
  for (const dir of subdirs) {
    exports[`./${dir}`] = `./${dir}/index.js`;
    exports[`./${dir}/*`] = `./${dir}/*`;
  }
  exports['./*'] = './*';

  pkg.exports = exports;

  writeFileSync(pkgFile, JSON.stringify(pkg, null, 4) + '\n');
  patched++;
}

console.log(
  `[patch-alfalab-esm] exports добавлено: ${patched}, уже на месте: ${skipped}` +
    (cleaned ? `, удалено старых артефактов: ${cleaned}` : ''),
);
