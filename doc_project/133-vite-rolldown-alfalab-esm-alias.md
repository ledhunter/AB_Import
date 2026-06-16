# 🎯 Vite 8 + Rolldown: резолвер `@alfalab/core-components-*/esm` для prod-build

## 📋 Описание

`vite build` (frontend, prod-stage Dockerfile) падает с ошибкой:

```
Error: [vite]: Rolldown failed to resolve import "@alfalab/core-components-config/esm"
  from "/app/node_modules/@alfalab/core-components-mq/esm/useIsDesktop.js".
```

Источник — переход Vite 8 на **Rolldown** в качестве сборщика. Rolldown строже
резолвит подпути пакетов: без явного поля `"exports"` в `package.json` он
**не раскрывает директорию** `@alfalab/core-components-config/esm` в
`esm/index.js`, хотя Node.js / Webpack / старый Rollup это делают по дефолту.

Пакеты `@alfalab/core-components-*` массово импортируют сиблингов через
подпуть `/esm` (директория, не файл), а их `package.json` содержат только
`"main"` / `"module"`, без `"exports"`. Это валидно для Node.js-резолвера,
но не валидно для строгого Rolldown.

---

## 🔍 Воспроизведение

```bash
# Любая команда vite build (в т.ч. внутри docker build --target prod)
cd KiloImportService.Web
npm run build
# → tsc -b OK
# → vite build → Rolldown error на @alfalab/core-components-config/esm
```

Касается ≥10 пакетов `@alfalab/core-components-*`, в каждом такие импорты:

```js
// node_modules/@alfalab/core-components-mq/esm/useIsDesktop.js
import { useCoreConfig } from '@alfalab/core-components-config/esm';
import { isFn }          from '@alfalab/core-components-shared/esm';
```

Структура целевого пакета:

```
@alfalab/core-components-config/
├── package.json  ← "main": "index.js", "module": "./esm/index.js", НЕТ "exports"
├── esm/
│   ├── index.js
│   ├── CoreConfigContext.js
│   └── ...
└── index.js (CJS)
```

Node-резолвер видит `esm` как директорию и автоматически берёт `esm/index.js`.
Rolldown — нет.

---

## ✅ Правильная реализация (v1.2 — custom resolver plugin)

`KiloImportService.Web/vite.config.ts`: собственный resolver-плагин,
который вызывает Node.js-`require.resolve` через `createRequire`:

```ts
import path from 'node:path';
import { createRequire } from 'node:module';
import type { Plugin } from 'vite';

const projectRoot = process.cwd();
const requireFromProject = createRequire(path.join(projectRoot, 'package.json'));

const alfalabEsmDirAlias: Plugin = {
  name: 'alfalab-esm-dir-alias',
  enforce: 'pre',  // ← до builtin резолвера Rolldown
  resolveId(source) {
    const match = /^(@alfalab\/core-components-[^/]+)\/esm$/.exec(source);
    if (!match) return null;
    try {
      // Возвращает абсолютный путь, проверенный Node-резолвером
      return requireFromProject.resolve(`${match[1]}/esm/index.js`);
    } catch {
      return null;  // пакет не установлен — даём резолву идти дальше
    }
  },
};

export default defineConfig(() => ({
  plugins: [alfalabEsmDirAlias, react()],
  // ...
}));
```

### ⚠️ Важно

- **`require.resolve` — канонический Node-резолвер**. Возвращает:
  - всегда АБСОЛЮТНЫЙ путь (требование Rolldown);
  - путь к РЕАЛЬНО СУЩЕСТВУЮЩЕМУ файлу (резолвер сам проверяет FS);
  - на Linux `/app/node_modules/@alfalab/core-components-config/esm/index.js`,
    на Windows `C:\…\node_modules\@alfalab\core-components-config\esm\index.js` —
    оба формата валидны для Rolldown без ручной POSIX-нормализации.
- **`createRequire(<absolute path>)`** — фиксирует базу резолва на корень
  проекта (`KiloImportService.Web`). НЕ зависит от `import.meta.url`
  (vite-config-loader даёт виртуальный URL временного bundle, что ломало
  `path.resolve(import.meta.url/.., 'node_modules')` в Docker — см. v1.1).
- **`enforce: 'pre'`** — плагин обязан срабатывать ДО builtin резолвера
  Rolldown, иначе тот первым попробует raw-подпуть и провалится.
- **Якорь `$` в regex обязателен** — иначе захватывается
  `@alfalab/core-components-X/esm/some-file`, и резолв файловых импортов
  внутри `/esm/` ломается.
- **`[^/]+`** — захватывает имя пакета без слешей, чтобы regex не «протекал»
  на `@alfalab/core-components/anything`.
- **`return null` на исключении** — если пакет не установлен, плагин молча
  отдаёт ход дальше, как полагается vite-резолверу. Не throw — это убило бы
  и легитимный fail-path.

### 📦 Подтверждённые пакеты-нарушители (всё покрывается одним regex'ом)

| Пакет | Импортирующий сиблинг | Файл |
|-------|----------------------|------|
| `@alfalab/core-components-config/esm` | `core-components-mq` | `esm/useIsDesktop.js:1` |
| `@alfalab/core-components-shared/esm` | `core-components-mq` | `esm/useIsDesktop.js:2` |
| `@alfalab/core-components-stack-context/esm` | `core-components-popover` | `esm/Component.js:12` |

В `node_modules/@alfalab` всего **~123 пакетов** с подпапкой `esm/` — каждый
потенциально импортируется сиблингом через `/esm`-подпуть. Regex покрывает все.

---

## ❌ Типичные неправильные обходы

### ❌ 0. Относительный путь в `resolve.alias.replacement` (v1.0 — отозвано)

```ts
// ОТОЗВАНО — Rolldown не раскрывает относительный path
alias: [{ find: /…/, replacement: '$1/esm/index.js' }],
```

Локальная сборка проходила за счёт кешей dev-резолвера, prod-build в Docker
падал с `vite-alias rewrote … but was not an absolute path` +
`UNLOADABLE_DEPENDENCY`.

### ❌ 0b. Абсолютный путь от `import.meta.url` в `resolve.alias.replacement` (v1.1 — отозвано)

```ts
// ОТОЗВАНО — import.meta.url в vite.config.ts указывает на ВРЕМЕННЫЙ bundle
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const nodeModulesAbs = path.resolve(__dirname, 'node_modules').replace(/\\/g, '/');
alias: [{ find: /…/, replacement: `${nodeModulesAbs}/$1/esm/index.js` }],
```

Vite предкомпилирует `vite.config.ts` в bundle перед загрузкой; `import.meta.url`
в этом bundle указывает на временный файл, не на исходный `/app/vite.config.ts`.
В итоге `path.resolve(__dirname, 'node_modules')` давал нерабочую базу и
Rolldown падал с `UNLOADABLE_DEPENDENCY` на путь `node_modules/@alfalab/…`
(Vite показывает relative-к-cwd, но реальный путь не существовал).

В v1.2 заменено на custom resolver-плагин, который не зависит от
`import.meta.url` и не строит строку вручную — отдаёт `require.resolve()`.

### ❌ 1. `build.rollupOptions.external`

```ts
// НЕПРАВИЛЬНО — externalize ломает runtime
build: {
  rollupOptions: {
    external: ['@alfalab/core-components-config/esm'],
  },
},
```

Сборка пройдёт, но в браузере получит `Cannot find module '@alfalab/core-components-config/esm'`.

### ❌ 2. Откат `vite` на 7.x

Технически работает (vite 7 использует rollup, не rolldown), но тянет за собой
понижение `@vitejs/plugin-react` на 4.x, потерю фич Vite 8, обновление lockfile.
Слишком крупная правка под одну ошибку резолва.

### ❌ 3. Прибить версию `@alfalab/core-components` под рукой

Не решает корневую проблему — следующие версии пакета продолжат импортить
сиблингов по подпути `/esm` (это их внутренний контракт).

---

## 📍 Применение в проекте

| Файл | Что |
|------|-----|
| [KiloImportService.Web/vite.config.ts](../KiloImportService.Web/vite.config.ts) | `resolve.alias` с regex |

---

## 🎯 Чек-лист (когда что-то снова сломалось)

- [ ] В логе сборки фраза `Rolldown failed to resolve import`?
- [ ] Импорт идёт на подпуть пакета **без расширения и слеша после `/esm`**?
- [ ] У целевого пакета в `package.json` отсутствует поле `"exports"`?
- [ ] Если все три «да» — добавить аналогичный regex-alias в `vite.config.ts`.

---

## 🔗 См. также

- [Vite 8 release notes — Rolldown bundler](https://vite.dev/guide/rolldown.html)
- [Rolldown migration — strictly enforce exports](https://rolldown.rs/guide/migration.html)
- [doc 13 — Vite proxy backend](./13-vite-proxy-backend.md) — другие настройки `vite.config.ts`
- [doc 132 — Build alignment с эталоном service-dev](./132-build-alignment-with-service-dev-reference.md) — общий контекст сборки
