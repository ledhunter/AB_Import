# 🩹 Postinstall-патч `exports` у `@alfalab/core-components-*`

## 📋 Описание

Vite 8 / Rolldown не резолвит подпуть `<pkg>/esm` у пакетов
`@alfalab/core-components-*` — потому что у них нет поля `exports` в
`package.json` (только `main` и `module`). Описано в [doc 133](./133-vite-rolldown-alfalab-esm-alias.md).

doc 133 предлагал custom-resolver-плагин в `vite.config.ts` — он работает
локально на Windows, но **не работает в Docker** (Vite предкомпилирует
конфиг во временный bundle, `process.cwd()` уходит мимо `/app`,
`createRequire().resolve()` бросает MODULE_NOT_FOUND, плагин молча
отдаёт ход дальше).

## 🧪 Эволюция фикса

| Версия | Подход | Локально | Docker | Причина смены |
|---|---|---|---|---|
| **v1.0** | `<pkg>/esm/package.json:{main:"./index.js"}` (sub-package.json трюк) | ✅ | ❌ | Resolution sub-package на Alpine Linux Rolldown игнорирует, на Windows работает |
| **v2.0** | `<pkg>/esm.js` со `export * from './esm/index.js'` (flat shim) | ❌ | ❌ | Rolldown при subpath-резолве в пакете без `exports` идёт в подпапку, файл-shim в корне игнорирует |
| **v3.0** | Добавляем поле `exports` в основной `<pkg>/package.json` **+** явные peer-deps в корневом `package.json` | ✅ | ✅ | Node.js Package Exports — стандарт, Rolldown уважает одинаково на всех платформах |

**Актуальная стратегия — v3.0.** Подтверждена локально (`✓ 998 modules transformed. ✓ built in 1.27s`) и в Jenkins Docker (2026-06-16, сборка успешна).

---

## ✅ Реализация v3.0

### Скрипт-патчер

`KiloImportService.Web/scripts/patch-alfalab-esm.mjs`:

```js
// Фильтр: патчим только подкомпоненты, НЕ метапакет.
// Метапакет `@alfalab/core-components` (без суффикса) имеет сложные
// подпути `<pkg>/select/desktop` — добавление exports с pass-through
// сломает auto-index resolution на вложенных папках.
if (!name.startsWith('core-components-')) continue;

// Собираем exports: все корневые подпапки с index.js + pass-through.
const subdirs = readdirSync(pkgRoot).filter(/* dir & has index.js */);

const exports = {
  '.': {
    import: norm(pkg.module || 'esm/index.js'),
    require: norm(pkg.main || 'index.js'),
    default: norm(pkg.main || 'index.js'),
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
```

### ⚠️ Важно

- **`if (!name.startsWith('core-components-')) continue;`** — пропускаем
  метапакет `@alfalab/core-components` и любое другое имя без суффикса.
  У метапакета сложные подпути типа `<pkg>/select/desktop` (директория
  внутри директории), и `"./*": "./*"` сломает их доступ — Node.js
  Package Exports НЕ делает auto-index resolution для wildcards.
- **Динамически перечисляем все корневые подпапки с `index.js`** — у
  `core-components-select` есть `desktop/`, `mobile/`, `responsive/`,
  у `core-components-mq` есть `cssm/`, `modern/`, `moderncssm/`. Каждой —
  явный entry в exports, иначе при `<pkg>/desktop` Rolldown получит путь
  к директории без entry → ERR_PACKAGE_PATH_NOT_EXPORTED.
- **`exports."."` с тремя ключами (`import`/`require`/`default`)** —
  пакеты `core-components-*` поставляются и как CJS (root `index.js`),
  и как ESM (`esm/index.js`). Rolldown в зависимости от контекста
  импортирующего файла выберет нужный.
- **`"./*": "./*"` в конце** — pass-through для всех остальных файлов
  (CSS, .json и т.д.). Безопасно при наличии явных subpath-записей выше.
- **Если у пакета УЖЕ есть `exports`** (мейнтейнеры подняли версию и
  сами добавили) — патч его НЕ трогает (`if (pkg.exports !== undefined) skip`).
- **Чистка артефактов прошлых версий** (`esm/package.json` v1.0, `esm.js` v2.0)
  делается при каждом запуске постинсталла — идемпотентно.

### Подключение

`KiloImportService.Web/package.json`:

```json
{
  "scripts": {
    "postinstall": "node scripts/patch-alfalab-esm.mjs"
  }
}
```

### Docker

`KiloImportService.Web/Dockerfile` (build- и dev-stages):

```dockerfile
COPY package.json package-lock.json* ./
# scripts/ нужен ДО `npm install`: postinstall запустит патчер.
COPY scripts/ scripts/
RUN npm install --legacy-peer-deps --no-audit --no-fund
```

`COPY scripts/` обязательно ДО `RUN npm install`, иначе `postinstall`
упадёт с `ENOENT: scripts/patch-alfalab-esm.mjs`.

---

## ❌ Чего НЕ делать

### ❌ Патчить метапакет `@alfalab/core-components` (без суффикса)

```js
// НЕПРАВИЛЬНО — у метапакета сложные подпути <pkg>/select/desktop
for (const name of readdirSync(root)) {
  /* добавляем exports каждому, включая core-components без суффикса */
}
```

Импорт `@alfalab/core-components/select/desktop` (типичный паттерн
использования метапакета) сломается:
`TS2305: Module '...' has no exported member 'SelectDesktop'`.

### ❌ Использовать sub-package.json (v1.0)

```js
// НЕПРАВИЛЬНО — кросс-платформенно ненадёжно
writeFileSync(join(esmDir, 'package.json'), '{"main":"./index.js"}');
```

На Windows-локали Rolldown это уважает, в Docker Alpine Linux — нет.

### ❌ Использовать esm.js shim (v2.0)

```js
// НЕПРАВИЛЬНО — Rolldown ищет в подпапке, файл в корне не проверяет
writeFileSync(join(pkgRoot, 'esm.js'), "export * from './esm/index.js';");
```

При subpath-импорте `<pkg>/esm` Rolldown идёт в подпапку `esm/`, файл
`esm.js` рядом с подпапкой игнорирует.

### ❌ Не указывать всех корневых подпапок в exports

```json
// НЕПРАВИЛЬНО — `<pkg>/desktop` сломается
{
  "./esm": "./esm/index.js",
  "./*": "./*"
}
```

Pass-through `"./*": "./*"` мапит `<pkg>/desktop` в `./desktop` (директория),
а Node.js Package Exports не делает auto-index resolution. Нужно явно
указать `./desktop`: `./desktop/index.js` для каждой подпапки с `index.js`.

### ❌ Откат на vite 7.x

Технически работает (rollup, не rolldown), но тянет понижение
`@vitejs/plugin-react`, потерю фич Vite 8, обновление lockfile.
Слишком крупная правка под одну проблему.

---

## 📦 Покрытие

Скрипт пробегает по `node_modules/@alfalab/core-components-*` (~121 пакет).
Патчится каждый, у которого:
- имя начинается с `core-components-` (с суффиксом),
- есть подпапка `esm/` с `esm/index.js`,
- ещё нет поля `exports` в `package.json`.

### ⚠️ Peer-dependencies, которые НЕ ставятся автоматически

Пакеты `@alfalab/core-components-mq`, `core-components-popover` и др.
объявляют ряд других `core-components-*` пакетов как **peerDependencies**.
С флагом `--legacy-peer-deps` (нужен из-за react@19 vs react-popper@2)
npm не устанавливает peer-deps автоматически — **ни на Windows локали,
ни в Docker Alpine**. До v3.0 это маскировалось более ранним фейлом
Rolldown на subpath `/esm`; после v3.0 (когда subpath резолвится) сборка
доходит до корневого CJS-импорта `require('@alfalab/core-components-config')`
и валится `Rolldown failed to resolve import "@alfalab/core-components-config"`.

**Решение**: явно добавить нужные peer-deps в `dependencies` корневого
`package.json` веб-проекта. Транзитивный список peer-deps `@alfalab/*`
у нас короткий — два пакета:

```json
{
  "dependencies": {
    "@alfalab/core-components-config": "^1.1.0",
    "@alfalab/core-components-stack-context": "^1.0.1"
  }
}
```

Получены через:

```bash
for f in node_modules/@alfalab/core-components-*/package.json; do
  node -e "const p=require('./$f'); for(const k in p.peerDependencies||{})
    if(k.startsWith('@alfalab/')) console.log(k+' '+p.peerDependencies[k])"
done | sort -u
```

Если в будущем `@alfalab/core-components` подтянет новые версии с
дополнительными peer-deps `@alfalab/*` — пересобирать список этим
скриптом и обновлять `dependencies`.

---

## 📍 Применение в проекте

| Файл | Что |
|------|-----|
| [KiloImportService.Web/scripts/patch-alfalab-esm.mjs](../KiloImportService.Web/scripts/patch-alfalab-esm.mjs) | патчер `exports` |
| [KiloImportService.Web/package.json](../KiloImportService.Web/package.json) | npm-хук `postinstall` |
| [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) | `COPY scripts/` до `RUN npm install` (build + dev stage) |
| [KiloImportService.Web/vite.config.ts](../KiloImportService.Web/vite.config.ts) | страховочный плагин `alfalabEsmDirAlias` (см. doc 133) — оставлен для случаев, когда patches не накатились |

---

## 🧪 Локальная проверка

```powershell
cd KiloImportService.Web

# Прогон патчера руками (для отладки):
node scripts/patch-alfalab-esm.mjs
# → [patch-alfalab-esm] exports добавлено: 119, уже на месте: 0

# Идемпотентность:
node scripts/patch-alfalab-esm.mjs
# → [patch-alfalab-esm] exports добавлено: 0, уже на месте: 119

# Build:
npm run build
# → ✓ 998 modules transformed
# → ✓ built in 831ms
```

В Docker `RUN npm install` сам запустит postinstall, патчер добавит
exports, и `npm run build` пройдёт.

---

## 🎯 Чек-лист (если снова сломалось)

- [ ] В Jenkins-логе всё та же `Rolldown failed to resolve … /esm`?
- [ ] В Dockerfile есть `COPY scripts/ scripts/` ДО `RUN npm install`?
- [ ] `package.json` содержит `"postinstall": "node scripts/patch-alfalab-esm.mjs"`?
- [ ] В логе сборки появилась строка `[patch-alfalab-esm] exports добавлено: N`?
      Если нет — патч не запускался.
- [ ] Если ошибка вида `Rolldown failed to resolve import "@alfalab/core-components-X"`
      БЕЗ подпути `/esm` — значит пакет `X` не установлен (peer-dep + `--legacy-peer-deps`).
      Добавь его в `dependencies` корневого `package.json`. Список нужных peer-deps
      собирается командой из секции «Peer-dependencies» выше.
- [ ] Если ошибка на конкретный подпуть (`<pkg>/desktop`, `<pkg>/select/something`)
      — проверь, есть ли подпапка в корне пакета и добавлена ли она в exports.
- [ ] Если падает на метапакете `@alfalab/core-components/select/desktop`
      — патч на метапакет не должен заходить (фильтр
      `if (!name.startsWith('core-components-'))`). Проверь скрипт.

---

## 🔗 См. также

- [doc 133](./133-vite-rolldown-alfalab-esm-alias.md) — первая попытка через
  vite-плагин (работает локально, не работает в Docker)
- [doc 132](./132-build-alignment-with-service-dev-reference.md) — общий
  контекст сборки в корп. контуре
- [Node.js Package Exports spec](https://nodejs.org/api/packages.html#exports)
