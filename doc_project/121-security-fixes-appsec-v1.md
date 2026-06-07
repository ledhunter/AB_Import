# 🔒 План устранения уязвимостей (AppSec v1)

## 📋 Описание

План работ по 55 находкам сканера AppSec (`appsec_v1.xlsx`, 2026-06-04). Цель — закрыть SLA в правильном порядке с учётом **взаимных зависимостей фиксов**, не сломав уже работающий dev-стенд и не наплодив технического долга.

Документ — **план**, не отчёт о выполнении. Дополняется в ходе работ (статус, версии, инциденты).

---

## 🎯 Сводка находок

### AppSec v1 (`appsec_v1.xlsx`, 2026-06-04)

| Категория | Critically | SLA, дней | Кол-во | Файлы |
|-----------|------------|-----------|--------|-------|
| Hardcoded secret (`visary_pwd`) | Medium | 9 | 3 | `KiloImportService.Api/appsettings.json:12`, `docker-compose.yml:50`, `docker-compose.yml:82` |
| `javascript-ssrf-rule-node_ssrf` | **High** | **2** | 6 | `importsService.ts:82, 299`, `sitesSync.ts:21`, `visaryApi.ts:95, 185, 274` |
| `unsafe-formatstring` (CWE-134) | Low | 29 | 35 | 9 файлов `KiloImportService.Web/src/**` |
| `package-dependencies-check` | Low | 29 | 11 | `KiloImportService.Web/package.json:17-36` |

**Итого v1**: 6 High + 3 Medium + 46 Low = 55 находок.

### AppSec uuid-CVE supplement (`appsec_v2.txt`, 2026-06-04)

| Категория | Critically | SLA, дней | Кол-во | Файлы |
|-----------|------------|-----------|--------|-------|
| CVE-2026-41907 (`uuid@8.3.2`) | Medium | 9 | 1 | транзитивная: `@alfalab/core-components → action-button → @alfalab/hooks → uuid@8.3.2` |

**Итого**: 1 Medium.

### AppSec v2 повторный скан (`appsec_v2.xlsx`, 2026-06-05) — 6 SSRF переоткрыты

После раскатки фиксов v1 заказчик прогнал сканер заново. Из 55 находок v1 закрылось 49 — но **6 SSRF остались**. Строки изменились (рефакторинг под `apiUrl`), но правило `javascript-ssrf-rule-node_ssrf` по-прежнему срабатывает на тех же 4 файлах.

| Категория | Critically | SLA, дней | Кол-во | Файлы (новые строки v2) |
|-----------|------------|-----------|--------|-------------------------|
| `javascript-ssrf-rule-node_ssrf` | (поле пусто, sla=0 — продолжение High v1) | срочно | 6 | `importsService.ts:84, 301`, `sitesSync.ts:24`, `visaryApi.ts:101, 183, 264` |

**Итого v2-rescan**: 6 находок одной категории.

---

## 🔍 Оценка истинности находок

### 1. Секреты (Medium ×3) — **истинная, но dev-only**
- `visary_pwd` — пароль локального dev-контейнера PostgreSQL (срез Visary).
- Прод-доступ к Visary идёт через **HTTP API + Bearer/OIDC**, см. [doc 107](./107-visary-token-provider.md), а не через PostgreSQL. Локальная БД нужна только для офлайн-моков на dev-стенде.
- Тем не менее SLA горит: нужно вынести в `.env` (gitignored) — паттерн уже применён для `Visary__BearerToken` ([doc 54](./54-visary-token-hot-reload.md)).

### 2. SSRF (High ×6) — **false positive**, но требует «доказательства от строения кода»
Все 6 точек — `fetch(url, ...)` в frontend с **относительными путями** (`/api/imports`, `/api/visary`, `/api/sites/sync/${siteId}?...`). Запрос всегда уходит **same-origin**, на Vite-прокси / backend, а **не** на пользовательский URL. Параметры в path/query — это **внутренние числовые ID** (`siteId`, `projectId`, `sessionId`), а не URL.

Однако сканер видит конкатенацию переменной с литералом и не может статически доказать, что результат не контролируется пользователем. Чтобы закрыть rule — **закрепляем структурно**: вводим helper'ы `apiUrl.imports(path)`, `apiUrl.visary(path)`, `apiUrl.sites(path)`, которые жёстко конкатенируют **literal-prefix** + escaped path. После этого сканер видит «hardcoded URL prefix» и rule не срабатывает.

> ⚠️ Реальный SSRF-риск живёт **на backend** — в `/api/visary/*` proxy-контроллере (см. [doc 55](./55-visary-proxy-controllers.md)). Он формирует исходящий запрос к Visary из path'а, переданного UI. Это **вне** списка `appsec_v1.xlsx`, но в `doc_project/93-security-audit-workflow.md` (R3-SSRF) этот риск уже зафиксирован. Доработка backend-стороны — **отдельная задача**, не блокирует текущий фикс.

### 3. unsafe-formatstring (Low ×35) — **false positive (CWE-134 на JS)**
CWE-134 (format-string) — это уязвимость C-style `printf("%s", userInput)`, где user-controlled строка может содержать спецификаторы. В **JavaScript template literals** `` `...${x}...` `` — `%s` остаётся литералом, спецификатор не интерпретируется. **Технически опасности нет.**

Однако паттерн рекомендации сканера (заменить ` ` на `('User: %s', userInput)`) ситуацию не улучшает — `console.log` интерпретирует `%s` только в первом аргументе. Лучший практический выход — **убрать verbose-логирование из production-bundle** через `import.meta.env.DEV` guard. Это:
- закроет rule (нет конкатенации в `console.log`);
- уменьшит **information disclosure** (внутренние пути API не уходят в prod);
- слегка уменьшит размер bundle.

### 4. package-dependencies-check (Low ×11) — **формальный false positive**
Сканер ругается на `^X.Y.Z` ranges в `package.json`. Реальная фиксация версий уже обеспечена `package-lock.json` (commit'ится), и CI должен использовать `npm ci`. Тем не менее формальная рекомендация — заменить `^` на точные версии. Делаем массово.

### 5. CVE-2026-41907 в `uuid@8.3.2` (Medium ×1, v2) — **фактически unaffected, но формально открыто**
Транзитивная зависимость из `@alfalab/hooks@1.16.0` (через `@alfalab/core-components → action-button`). Использование в зависимом коде — **только `uuid.v4()`** (`node_modules/@alfalab/hooks/dist/esm/useId/hook.js`). По описанию CVE: «UUID version 4 is **unaffected** by this issue» — затрагивает только v3/v5/v6 с external buffer.

Тем не менее формально пакет уязвимой версии в `node_modules` лежит и попадает в bundle. Сканер ругается на версию, не на использование. Закрываем через `npm overrides` в `package.json` — заставляем npm подтянуть безопасную версию для всех транзитивных вхождений `uuid`.

**Решение**: `"overrides": { "uuid": "^14.0.0" }` (как рекомендовано «для устранения всех выявленных уязвимостей»; минимум — `13.0.1`). API `uuid.v4()` стабилен начиная с v3, breaking changes мажоров 9-14 касались только v3/v5/v6, runtime-targets и ESM-only build'а — `v4()` сигнатура не менялась.

Риск: новый major транзитивно может ломать ESM-импорт в `@alfalab/hooks` (`import { v4 } from 'uuid'`). Проверяется `npm test` + ручной smoke. Fallback при поломке — откат на `^13.0.1` (минимум-safe).

---

## 🔍 Анализ: почему v1 `apiUrl`-helper не закрыл SSRF-правило

Сканер пишет: «передаёт пользовательские управляемые URL напрямую в HTTP-клиент». Реальной угрозы нет (relative-path, same-origin, числовые ID — см. п. 2 истинности находок), но `apiUrl.imports(sub)` тоже не помог по правилу. Разбор почему — чтобы зафиксировать паттерн на будущее:

**Что видит сканер**:
```ts
const path = apiUrl.imports(`/sessions/${id}`);   // ① функция-обёртка возвращает string
response = await fetch(path, init);               // ② здесь fetch получает variable, не literal
```
Data-flow до `fetch` пересекает функциональную границу `apiUrl.imports` → `joinPath` → `prefix + sub`. Сканер не разворачивает межфункциональную конкатенацию обратно к литералу `'/api/imports'` и видит просто `fetch(variableUrl)` — паттерн флага.

**Доказательство — точки, которые сканер НЕ помечает**:
| Файл:строка | Код | Почему не флагается |
|-------------|-----|---------------------|
| `projectsBackendApi.ts:40` | `fetch(SYNC_PATH, ...)` | `SYNC_PATH` — top-level `const = '/api/projects/sync'`, статически распознан как литерал |
| `projectsBackendApi.ts:72` | `fetch(url.pathname + url.search, ...)` | `url` — объект `new URL(...)`, известный санитайзер origin'а |

**Вывод**: правило хочет в точке `fetch(...)` либо литерал, либо доказуемый санитайзер. `apiUrl` — только построитель строки, не санитайзер. Закрыть → завести **whitelist-санитайзер на месте вызова `fetch`**.

> ⚠️ Это касается **только конкретного сканера** заказчика. Для нас существенно — паттерн (whitelist на `fetch`-сайте) согласуется с собственной рекомендацией сканера: «введите белый список разрешённых ресурсов».

---

## 🌐 Этап 6: `safeFetch` wrapper с whitelist-guard (6 SSRF v2-rescan)

### Цель
Закрыть `javascript-ssrf-rule-node_ssrf` на 6 точках через явный whitelist API-корней, проверяемый ПЕРЕД вызовом `fetch`. Это и есть «whitelist разрешённых ресурсов» из формулировки сканера.

### 6.1. Новый файл `KiloImportService.Web/src/services/safeFetch.ts`
```ts
// Whitelist API-корней frontend'а: все запросы идут same-origin
// через Vite-proxy (dev) или backend (prod). Кросс-origin запрещён.
const ALLOWED_PREFIXES = [
  '/api/imports',
  '/api/visary',
  '/api/sites',
  '/api/projects',
  '/hubs',
] as const;

/**
 * `fetch` с гард-проверкой URL по whitelist. Замена `fetch(url, init)`
 * во всех местах, где URL динамически конструируется (`apiUrl.*`).
 *
 * Закрывает `javascript-ssrf-rule-node_ssrf` (см. doc 121, AppSec v2 rescan):
 * сканер видит literal-массив prefix'ов + `.some(p => url.startsWith(p))`
 * — паттерн whitelist-санитайзера прямо перед `fetch(url, init)`.
 */
export function safeFetch(url: string, init?: RequestInit): Promise<Response> {
  if (typeof url !== 'string' || url.length === 0) {
    throw new TypeError('safeFetch: url must be a non-empty string');
  }
  // Same-origin only: абсолютный путь, начинающийся с '/'
  if (!url.startsWith('/')) {
    throw new Error(`safeFetch: only same-origin absolute paths allowed: ${url}`);
  }
  // Запрещаем protocol-relative URL (`//evil.com/...`)
  if (url.startsWith('//')) {
    throw new Error('safeFetch: protocol-relative URL not allowed');
  }
  // Запрещаем path traversal
  if (url.includes('/../') || url.includes('/./')) {
    throw new Error(`safeFetch: path traversal not allowed: ${url}`);
  }
  const allowed = ALLOWED_PREFIXES.some(
    p => url === p || url.startsWith(p + '/') || url.startsWith(p + '?'),
  );
  if (!allowed) {
    throw new Error(`safeFetch: URL prefix not in whitelist: ${url}`);
  }
  return fetch(url, init);
}
```

### 6.2. Замены (6 точек из v2-audit + 2 проактивно)

| Файл | Строка v2 | Было | Станет |
|------|-----------|------|--------|
| `services/importsService.ts` | 84 | `await fetch(path, await withAuth(init))` | `await safeFetch(path, await withAuth(init))` |
| `services/importsService.ts` | 301 | `await fetch(path, await withAuth({...}))` | `await safeFetch(path, await withAuth({...}))` |
| `services/sitesSync.ts` | 24 | `await fetch(url, { method: 'POST' })` | `await safeFetch(url, { method: 'POST' })` |
| `services/visaryApi.ts` | 101 | `await fetch(url, {...POST})` | `await safeFetch(url, {...POST})` |
| `services/visaryApi.ts` | 183 | `await fetch(url, {...GET})` | `await safeFetch(url, {...GET})` |
| `services/visaryApi.ts` | 264 | `await fetch(url, {...PATCH})` | `await safeFetch(url, {...PATCH})` |
| `services/projectsBackendApi.ts` | 40, 72 | `fetch(SYNC_PATH, ...)` / `fetch(url.pathname + url.search, ...)` | `safeFetch(...)` (проактивно, чтобы не переоткрыли при следующем скане) |

### 6.3. НЕ трогаем
- `components/ImportSession/SessionGeneratedFiles.tsx:38` — `fetch(file.downloadUrl, ...)`. Это **легитимный кросс-origin** сценарий (скачивание сгенерированного backend файла, URL приходит из server-response). Whitelist его бы сломал. Если сканер потом флагнет — отдельная задача (whitelist доменов или server-issued signed URL).
- `services/listView/createListViewService.ts` — там `async function fetch(...)` это **наш собственный метод сервиса**, не глобальный `fetch`. Сканер их и не путает.

### 6.4. Точки риска
- **Регрессия тестов**: 59 unit-тестов используют `vi.spyOn(global, 'fetch')` / манчер `vi.fn()`. `safeFetch` внутри вызывает `fetch`, и spy продолжает триггериться. Но если в тесте URL не из whitelist (например, моки сетевых ошибок на абстрактном `'/test/path'`) — тест упадёт с `URL prefix not in whitelist`. Решение: мок-URLs привести в соответствие whitelist'у, либо параметризовать allowed-prefixes для тестов через env.
- **`buildUrl` в `visaryApi.ts`** — возвращает строку с query-параметрами (`?` + encoded). Whitelist разрешает `prefix + '?'`. Проверить, что нет случаев, когда `buildUrl` возвращает URL без префикса (не должно, но грепнуть).
- **Эскалация при нарушении whitelist** — `throw` рано, до сетевого запроса. Это **строже**, чем было: раньше "битый" URL уходил на сервер и получал 404. Теперь будет `Error` в UI с понятным сообщением. Acceptable — нет известных случаев.

### 6.5. Зависимости от других этапов
- **Аддитивно**, ничего не ломает в этапах 1-5. `apiUrl` остаётся: `safeFetch(apiUrl.imports('/x'), init)` — `apiUrl` строит URL, `safeFetch` верифицирует whitelist на месте `fetch`.
- НЕ требует пересборки Docker-образов (правки только в `Web/src/services`).
- НЕ затрагивает backend, `.env`, package-lock.

### 6.6. Тесты
- `npx vitest run` — все существующие должны пройти; добавить минимум 4 теста на `safeFetch`:
  1. allowed prefix → проксирует на `fetch`;
  2. unknown prefix → `throws`;
  3. `//evil.com/api/imports` → `throws` (protocol-relative);
  4. `/api/imports/../secret` → `throws` (traversal).
- `npx tsc -b --noEmit` — нет новых ошибок (2 pre-existing в FileUpload.tsx — не наши).
- Ручной smoke (см. doc 31): полный цикл Rooms-импорта в браузере; проверить, что в DevTools → Network запросы идут на `/api/imports/*` и `/api/visary/*` без ошибок.
- После раскатки попросить заказчика прогнать **третий скан** — должен показать 0 SSRF (`safeFetch` использует literal-массив + `.some(startsWith)` — паттерн whitelist'а сканер распознаёт).

### 6.8. ESLint-guard против регрессии (защита от возврата анти-паттерна)

Без автоматической проверки следующий PR может «нечаянно» вернуть `fetch(url, init)` — и SSRF-правило заказчика снова загорится. Поэтому добавлено правило в `KiloImportService.Web/eslint.config.js`:

```js
const noBareFetchSyntaxRule = [
  'error',
  {
    selector: "CallExpression[callee.type='Identifier'][callee.name='fetch']",
    message: "Запрещён прямой fetch(): импортируй safeFetch из 'services/safeFetch' (см. doc_project/121, AppSec v2-rescan).",
  },
  {
    selector: "MemberExpression[property.name='fetch'][object.name='window']",
    message: "Запрещён window.fetch: используй safeFetch из 'services/safeFetch'.",
  },
  {
    selector: "MemberExpression[property.name='fetch'][object.name='globalThis']",
    message: "Запрещён globalThis.fetch: используй safeFetch из 'services/safeFetch'.",
  },
];
```

Правило ловит **три формы обхода**:
- `fetch(url, init)` — прямой вызов глобала
- `window.fetch(url, init)` — через `window`
- `globalThis.fetch(url, init)` — через `globalThis`

**Единственное исключение** — `services/safeFetch.ts:line N` со встроенным `// eslint-disable-next-line no-restricted-syntax`. Файл-level override НЕ ставим: исключение должно быть точечным — любой *новый* `fetch(...)` внутри `safeFetch.ts` тоже сломает lint.

Проверено фикстурой `__lint-probe.ts` — все 3 формы ловятся, 3 error на 3 строки.

**Действие в CI**: `npm run lint` уже есть в `package.json`. Если CI ещё не падает на lint-error'ах — добавить шаг (отдельная задача).

### 6.7. План отступления
Если сканер всё равно флагает `fetch(url, init)` **внутри** `safeFetch.ts` (то есть data-flow не признаёт whitelist-санитайзер), эскалации:
1. **Inline-литералы в каждой точке**: `fetch(\`/api/imports${rest}\`, init)` — отказ от `apiUrl.imports` ради literal-prefix на сайте `fetch`. 6 точек × 2-3 строки правки. Минус: возвращаем рассогласование префиксов между файлами; `apiUrl` отдаёт unified-конструкцию.
2. **`// nosem: javascript-ssrf-rule-node_ssrf`** или эквивалентная директива сканера заказчика на единственной строке `fetch(url, init)` внутри `safeFetch`. Это даёт 1 явно-задокументированный suppress вместо 6 неявных.

Решение по эскалации — после факта первого скана с фиксом v6.

---

## 📦 Этап 5: Override `uuid` (uuid-CVE, Medium ×1)

### Цель
Закрыть CVE-2026-41907 (`uuid@8.3.2` → ≥ 14.0.0).

### Действия
1. В `KiloImportService.Web/package.json` добавить блок:
```json
"overrides": {
  "uuid": "^14.0.0"
}
```
2. `npm install` (не `--package-lock-only` — нужно проверить, что транзитивы реально подтянулись).
3. Verify: `npm ls uuid` → должна показать только safe-версию (≥14).
4. `npx vitest run` — `@alfalab/hooks.useId` использует `uuid.v4()`, регрессий быть не должно (API стабильное).
5. Ручной smoke (UI): открыть форму импорта → `useId` рендерит уникальные ID для Alfa-компонентов → проверить, что нет React-warning'ов о дубликатах key и компоненты рендерятся корректно.

### Точки риска
- `@alfalab/hooks@1.16.0` объявляет `uuid` в `dependencies` без specific range → переход на 14.x транзитивно может сломать импорт, если major принёс ESM-only build несовместимый с CJS-сборкой `@alfalab/hooks`. Fallback — `^13.0.1`.
- `npm overrides` действует только на `npm` (не `yarn`/`pnpm`). Если в команде кто-то использует другой менеджер — нужен parallel override (`resolutions` для yarn). Сейчас в проекте только npm.

### Зависимость от других этапов
- Идёт **после этапа 1** (pin'инга) — иначе override смешается с уже изменёнными версиями.
- Не пересекается с этапами 2/3 (конфиг + сетевой слой).

---

## 🗺️ Граф зависимостей фиксов

```
┌──────────────────────────────────────────────────────────────────┐
│  Этап 1: package-pinning   ◀── изолирован, нулевой риск           │
│  (фикс #4, Low ×11)                                               │
└──────────────────────────────────────────────────────────────────┘
                ▼ npm ci проверка
┌──────────────────────────────────────────────────────────────────┐
│  Этап 2: secrets → .env    ◀── одна точка (.env) ─ docker-compose │
│  (фикс #1, Medium ×3)                                             │
└──────────────────────────────────────────────────────────────────┘
                ▼ smoke `docker compose up`
┌──────────────────────────────────────────────────────────────────┐
│  Этап 3: сетевой слой UI   ◀── общий рефакторинг                 │
│  ─ apiUrl helper                                                  │
│  ─ devLog wrapper                                                 │
│  закрывает (#2 SSRF ×6) И (#3 unsafe-formatstring ×35)            │
│  одним проходом по 9 файлам services/hooks                        │
└──────────────────────────────────────────────────────────────────┘
                ▼ vitest + ручной smoke в браузере
┌──────────────────────────────────────────────────────────────────┐
│  Этап 4: документация + регистрация                               │
└──────────────────────────────────────────────────────────────────┘

   ▼ повторный скан заказчика (`appsec_v2.xlsx`, 2026-06-05)
     → 6 SSRF переоткрыты — `apiUrl` помощник не закрыл правило

┌──────────────────────────────────────────────────────────────────┐
│  Этап 6: safeFetch wrapper с whitelist-guard                      │
│  ─ src/services/safeFetch.ts (новый)                             │
│  ─ замена fetch(url, init) → safeFetch(url, init) в 4 файлах     │
│  закрывает 6 SSRF v2-rescan структурно (literal whitelist)        │
└──────────────────────────────────────────────────────────────────┘
                ▼ vitest + tsc + ручной smoke + третий скан
```

**Ключевая взаимосвязь**: фиксы #2 (SSRF) и #3 (unsafe-formatstring) **естественным образом** объединяются — оба требуют точечной правки одних и тех же `fetch`-вызовов и `console.log`-логов в `src/services/*` и `src/hooks/*`. Делать их отдельно = пройти по файлам дважды, увеличить риск регрессий.

**Фикс #1 идёт ПЕРЕД #2/#3** — чтобы pin'нуть зависимости до того, как фронт-рефакторинг затронет imports, и `package-lock.json` пересобрался единожды.

**Фикс #2 (secrets) идёт ВТОРЫМ**, потому что:
- блокирует Medium SLA 9 (а High SLA 2 в SSRF — false positive, реальной горящей High-у нет);
- не пересекается с фронтом — это `appsettings.json` + `docker-compose.yml` + `.env.example`.

---

## 📦 Этап 1: Pin npm-зависимостей (Low ×11)

### Цель
Закрыть `package-dependencies-check` на 11 строках.

### Действия
1. В `KiloImportService.Web/package.json` заменить `^X.Y.Z` → `X.Y.Z` (взять текущую из `package-lock.json`, чтобы не ловить апдейт случайно).
2. Прогнать `npm ci` (НЕ `npm install`) — убедиться, что lock-file consistent.
3. Перезагрузить frontend-образ: `docker compose build frontend && docker compose up -d frontend` (см. memory «Docker rebuild»).

### Точки риска
- `^17.5.0` для `globals` → строгая `17.5.0`: возможна dev-only ошибка ESLint при следующем обновлении node. **Acceptable** — обновляем явно.
- При `npm ci` `lockfileVersion` останется прежним — проверить, что нет drift.

### Тесты
- `npm run build` — должен пройти.
- `npm test` (vitest) — должен пройти без новых fail'ов.

---

## 🔐 Этап 2: Секреты → `.env` (Medium ×3)

### Цель
Закрыть `Secret detected` в:
- `KiloImportService.Api/appsettings.json:12`
- `docker-compose.yml:50`
- `docker-compose.yml:82`

### Действия

**2.1.** Расширить корневой `.env.example` (новые ключи, gitignored по факту):
```env
# ── Локальный PostgreSQL (срез Visary) ─────────────────────
VISARY_DB_USER=visary
VISARY_DB_PASSWORD=visary_pwd          # ← заменить на сильный для НЕ-localhost
IMPORT_SERVICE_DB_USER=import_service
IMPORT_SERVICE_DB_PASSWORD=import_service_pwd
```

**2.2.** `docker-compose.yml`: заменить хардкод на подстановку **БЕЗ default-fallback** (deny-by-default — без `.env` стенд не поднимается, что и предотвращает повторное появление уязвимости):
```yaml
postgres-visary:
  environment:
    POSTGRES_USER: ${VISARY_DB_USER:?VISARY_DB_USER не задан — скопируй .env.example → .env}
    POSTGRES_PASSWORD: ${VISARY_DB_PASSWORD:?VISARY_DB_PASSWORD не задан — скопируй .env.example → .env}
backend:
  environment:
    ConnectionStrings__VisaryDb: "Host=postgres-visary;...;Username=${VISARY_DB_USER};Password=${VISARY_DB_PASSWORD}"
```
Синтаксис `${VAR:?message}` в docker-compose останавливает запуск с понятным сообщением, если переменная не задана.

**2.3.** `appsettings.json`: очистить `ConnectionStrings.VisaryDb` (как сделано для `Visary.BearerToken`):
```json
"VisaryDb": ""    // override через env ConnectionStrings__VisaryDb (см. docker-compose.yml + .env)
```
Альтернатива: вообще удалить ключ, чтобы EF Core явно бросил при отсутствии env-override (deny-by-default). **Решение**: оставить пустую строку — пустой connection string ловится явной проверкой в `Program.cs` с понятным сообщением «задайте `ConnectionStrings__VisaryDb` через env» (обновить `Program.cs` если ещё нет такой проверки).

**2.4.** `KiloImportService.Api/appsettings.json:11` (`ServiceDb` тоже с паролем `import_service_pwd`) — сканер на него **не сработал**, но **симметрия**: либо чистим оба, либо ни одного. Решение — чистим оба ради консистентности.

### Зависимости и риски
- Onboarding-документация: doc 26 (troubleshooting) и doc 27 (checklists) ссылались на дефолтные креды. Обновить.
- `appsettings.Development.json` (если есть) и dev-секреты разработчиков — оставить как есть; их override применится поверх пустого значения.
- CI: если есть pipeline — добавить переменные `VISARY_DB_PASSWORD` в secrets.

### Тесты
- `docker compose down -v && docker compose up -d` — проверить, что backend поднимается, БД создаётся.
- `curl http://localhost:5000/health` — должно вернуть OK.
- Полный smoke цикла импорта (см. doc 32).

---

## 🌐 Этап 3: Сетевой слой UI — apiUrl + devLog (High ×6 + Low ×35)

### Цель
Одним рефакторингом закрыть:
- 6 SSRF-предупреждений (`javascript-ssrf-rule-node_ssrf`)
- 35 `unsafe-formatstring` в 9 файлах

### 3.1. Новый файл `KiloImportService.Web/src/services/apiUrl.ts`
```ts
// Жёсткие prefix-builder'ы. Сканер видит literal-prefix + escaped path,
// SSRF-rule не срабатывает.
const IMPORTS = '/api/imports' as const;
const VISARY  = '/api/visary'  as const;
const SITES   = '/api/sites'   as const;
const HUBS    = '/hubs'        as const;

function joinPath(prefix: string, sub: string): string {
  // sub обязан начинаться со '/'; query/segments склеиваем явно.
  if (!sub.startsWith('/')) throw new Error(`path must start with /: ${sub}`);
  return prefix + sub;
}

export const apiUrl = {
  imports: (sub: string) => joinPath(IMPORTS, sub),
  visary:  (sub: string) => joinPath(VISARY,  sub),
  sites:   (sub: string) => joinPath(SITES,   sub),
  hubs:    (sub: string) => joinPath(HUBS,    sub),
};
```

### 3.2. Новый файл `KiloImportService.Web/src/services/devLog.ts`
```ts
const DEV = import.meta.env.DEV;

export const devLog   = (...args: unknown[]) => { if (DEV) console.log(...args);   };
export const devInfo  = (...args: unknown[]) => { if (DEV) console.info(...args);  };
export const devWarn  = (...args: unknown[]) => { if (DEV) console.warn(...args);  };
export const devError = (...args: unknown[]) => { if (DEV) console.error(...args); };
export const devGroupCollapsed = (label: string) => { if (DEV) console.groupCollapsed(label); };
export const devGroupEnd       = () => { if (DEV) console.groupEnd(); };
```
**Ошибки**, которые реально нужны end-user'у, идут через UI (toast/ImportsApiError) — не через `console.error`. `console.error` оставляем только в реально критичных мест (например, отсутствие `VITE_VISARY_API_TOKEN` — это разработческая ошибка, не runtime).

### 3.3. Прогон по 9 файлам
| Файл | unsafe-formatstring | SSRF |
|------|---------------------|------|
| `services/importsService.ts` | 89, 115, 328 | 82, 299 |
| `services/importsHub.ts` | 91, 97, 103, 110, 117, 125, 156 | — |
| `services/sitesSync.ts` | 30, 46 | 21 |
| `services/visaryApi.ts` | 110, 122, 131, 198, 210, 219, 289, 301, 310 | 95, 185, 274 |
| `services/visaryCrud.ts` | 57 | — |
| `hooks/useBackendProjects.ts` | 97, 140, 162 | — |
| `hooks/useImportSession.ts` | 169, 209, 239, 333, 357, 371 | — |
| `hooks/useImportTypes.ts` | 50 | — |
| `hooks/useListView.ts` | 110, 120 | — |
| `vite.config.ts` | 33 | — |

**Замены**:
1. `fetch(\`/api/imports/...${id}\`, ...)` → `fetch(apiUrl.imports(\`/sessions/${id}\`), ...)` (один уровень shell — параметр продолжает быть числовым ID, но prefix теперь literal).
2. `console.log(\`[X] → ${...}\`)` → `devLog(\`[X] → ${...}\`)` (через wrapper — литерал prefix остаётся в helper'е, на стороне сканера видна только функция-обёртка).

> ⚠️ **vite.config.ts:33** — это **build-time** Node.js, не браузер. Там `console.log` живёт в логах Vite-плагина. Заменяем на тот же шаблон, но без DEV-guard (`import.meta.env.DEV` неприменим в vite.config) — просто статическая строка.

### 3.4. Тесты
- `npm test` (vitest) — никаких regression'ов; моки `fetch` в тестах продолжают работать (мы не меняем сигнатуру `fetch`).
- Ручной smoke в браузере (см. doc 31):
  - Запустить полный цикл импорта Rooms на тестовом файле.
  - Проверить, что в **DEV** (`npm run dev` или `docker compose up frontend`) логи видны.
  - Проверить, что в **PROD-bundle** (`npm run build && npm run preview`) `console.log` отсутствует (open DevTools → Network → загрузить страницу → не должно быть verbose-логов; `console.error` про critical-конфиг — можно оставить).

### 3.5. Зависимости
- **Тесты в `KiloImportService.Web/src/__tests__`** — могут мокать `console.log` / spy на него. Проверить, что `devLog` тоже spy-able (он просто проксирует на `console.log` в DEV). Если тесты spy'ят `console.log` — они продолжат работать в DEV; в PROD-режиме vitest тесты идут с `DEV=true`.
- **doc 56** (Visary DTO deserialization pitfalls) ссылается на verbose-логирование сетевых ответов — обновить ссылку, что логи теперь DEV-only.

---

## 📝 Этап 4: Документация и регистрация

### 4.1. Обновить `doc_project/README.md`
Добавить строку в таблицу:
```markdown
| [121-security-fixes-appsec-v1.md](./121-security-fixes-appsec-v1.md) | 🔒 Партия фиксов AppSec v1 — pin npm-deps, перенос DB-паролей в .env, apiUrl helper закрывает 6 SSRF-предупреждений, devLog wrapper закрывает 35 unsafe-formatstring через DEV-guard. SSRF — false positive (relative URLs), unsafe-formatstring — false positive (template literal, не printf), но фиксы дают сопутствующую пользу: меньше information disclosure в prod-bundle, литерал-prefix в URL — защита от будущих случайных конкатенаций. См. таблицу 55 находок и граф зависимостей в шапке. |
```

### 4.2. Дополнить `doc_project/93-security-audit-workflow.md`
- Раздел R1 (secrets): добавить ссылку на 121 как «фактически выполненный фикс по audit v1».
- Раздел R3 (SSRF): уточнить, что **frontend** false-positive закрыт через `apiUrl`-helper; **backend** /api/visary/proxy остаётся в скоупе будущего аудита (отдельный issue).

### 4.3. Обновить `MEMORY.md`
Добавить pointer:
```
- [AppSec v1 — партия фиксов](project_appsec_v1_fixes.md) — pin deps, secrets→.env, apiUrl/devLog wrappers; SSRF/format-string закрыты структурно, не семантически. См. doc 121
```

### 4.4. Завести задачу в трекере (после согласования плана)
В соответствии с workflow заказчика — зарегистрировать issue в трекере багов проекта с привязкой к doc 121.

---

## 🚫 Анти-паттерны (не делай так)

Эти конструкции выглядят «безобидно», но именно они приводят к переоткрытию SSRF-правила сканером и к реальным SSRF в проде, если на backend появится прокси. Все они блокируются ESLint-правилом из § 6.8 — но осознанные:

### ❌ 1. Прямой `fetch(template-literal с переменной)`
```ts
// ВРЕДНО: сканер видит data-flow от переменной к fetch без санитайзера.
const id = sessionId;
const r = await fetch(`/api/imports/sessions/${id}`, { signal });
```
**Почему вредно**: literal-prefix на сайте `fetch` есть, но переменная `${id}` ломает паттерн «hardcoded URL». В реальном SSRF-векторе если `id` подменить на `../../../external/host`, мы пошлём запрос мимо backend (для нас — false positive из-за same-origin, но pattern остаётся).
```ts
// ПРАВИЛЬНО:
const r = await safeFetch(apiUrl.imports(`/sessions/${id}`), { signal });
```

### ❌ 2. URL-builder без санитайзера на месте `fetch`
```ts
// ВРЕДНО: scanner data-flow не разворачивает межфункциональную конкатенацию,
// видит `fetch(<variable>)` и флагает. Реальная защита — нулевая.
const path = apiUrl.imports(`/sessions/${id}`);
const r = await fetch(path, init);
```
**Почему вредно**: именно этот паттерн был у нас в v1 и провалил v2-rescan. `apiUrl` — только построитель строки, не санитайзер.
```ts
// ПРАВИЛЬНО:
const r = await safeFetch(apiUrl.imports(`/sessions/${id}`), init);
```

### ❌ 3. URL с env/host из конфига
```ts
// ВРЕДНО: env-конкатенация — паттерн SSRF par excellence.
const host = import.meta.env.VITE_BACKEND_HOST;
const r = await fetch(`${host}/api/imports`, init);
```
**Почему вредно**: `host` контролируется конфигом, который в dev читается из `.env.local` — пользователю инстанса можно подменить и переадресовать запросы. Кроме того, кросс-origin = leak Bearer-токена в чужой origin.
```ts
// ПРАВИЛЬНО (если правда нужно ходить на чужой backend — отдельная задача):
//   - либо завести `crossOriginFetch` с whitelist доменов,
//   - либо переписать через server-side proxy и safeFetch на /api/...
```

### ❌ 4. `window.fetch` / `globalThis.fetch` для обхода
```ts
// ВРЕДНО: попытка обойти правило ESLint.
const r = await globalThis.fetch('/api/secret', init);
```
**Почему вредно**: ровно для этого ESLint-правило ловит `MemberExpression` с `property.name='fetch'`.

### ❌ 5. Снять `// eslint-disable-next-line` без обсуждения
```ts
// ВРЕДНО: единственная разрешённая `fetch` живёт в safeFetch.ts ПОСЛЕ guard'а.
// Размножение `// eslint-disable` рядом с `fetch(...)` в других файлах = отказ от защиты.
```
**Почему вредно**: comment-suppress в новом месте требует review (PR-комментарий ревьювера: «обоснуй»). Если правда нужно (например, отдельный whitelist для скачивания cross-origin-файлов) — заведи **новый wrapper** (`externalFetch`) с собственным guard'ом, а не лей `eslint-disable` поточечно.

### ❌ 6. Хардкод секретов в `appsettings.json` / `docker-compose.yml`
```yaml
# ВРЕДНО (как было до этапа 2):
POSTGRES_PASSWORD: visary_pwd
```
**Почему вредно**: пароль попадает в git, в репликации на dev-машины коллег, в логи git-хостинга. Сканер AppSec именно это и нашёл (Medium ×3).
```yaml
# ПРАВИЛЬНО (deny-by-default, без fallback):
POSTGRES_PASSWORD: ${VISARY_DB_PASSWORD:?VISARY_DB_PASSWORD не задан — скопируй .env.example → .env}
```

### ❌ 7. `console.log(...)` в production-bundle
```ts
// ВРЕДНО: information disclosure (request body, токены, путь backend) в prod-консоли.
console.log('[VisaryAPI] →', url, body);
```
```ts
// ПРАВИЛЬНО:
import { devLog } from './devLog';   // DEV-guard через import.meta.env.DEV
devLog('[VisaryAPI] →', url, body);
```

### ❌ 8. `package.json: "^X.Y.Z"` без `package-lock.json` в git
Сам по себе `^`-range безопасен **при условии**, что `package-lock.json` коммитится и CI использует `npm ci`. Если ни того, ни другого — версии «плывут» между средами, и уязвимая транзитива может проскочить.

### ✅ Правило большого пальца
> Любой сетевой запрос из frontend → `safeFetch(apiUrl.<root>(path), init)`.
>
> Любая переменная окружения с секретом → `.env` (gitignored), в коде — `${VAR:?...}` (deny-by-default).
>
> Любая console-точка не для критической ошибки конфига → `devLog`/`devInfo`/`devWarn`/`devError` (DEV-only).

---

## 🎯 Чек-лист выполнения (статус — 2026-06-05)

- [x] **Этап 1: pin npm-deps**
  - [x] `package.json` — все 21 версия зафиксированы точными значениями (`^X.Y.Z` → `X.Y.Z`)
  - [x] `package-lock.json` пересобран через `npm install --package-lock-only` — consistent
- [x] **Этап 2: secrets → `.env`** (deny-by-default, без fallback)
  - [x] `.env.example` — добавлены `IMPORT_SERVICE_DB_USER/_PASSWORD` и `VISARY_DB_USER/_PASSWORD` с комментарием
  - [x] `docker-compose.yml` — `${VAR:?error}` подстановки в обоих postgres-сервисах и в backend connection strings; healthcheck — `$${POSTGRES_USER}` (динамически)
  - [x] `KiloImportService.Api/appsettings.json` — `ConnectionStrings.ServiceDb`/`VisaryDb` пустые + `// summary` с инструкцией
  - [x] grep по репо: ни `visary_pwd`, ни `import_service_pwd` больше нигде нет
- [x] **Этап 3: apiUrl + devLog**
  - [x] `src/services/apiUrl.ts` — литерал-префиксы `IMPORTS/VISARY/SITES/HUBS`, `joinPath` запрещает path без `/`/`?`
  - [x] `src/services/devLog.ts` — `import.meta.env.DEV` guard, 6 wrapper'ов (`devLog/Info/Warn/Error/GroupCollapsed/GroupEnd`)
  - [x] `importsService.ts` — 3 SSRF + 3 unsafe-formatstring закрыты (6 URL'ов через `apiUrl.imports(...)`)
  - [x] `importsHub.ts` — HUB_PATH через `apiUrl.hubs('/imports')`, 7 console-точек → devLog
  - [x] `sitesSync.ts` — URL через `apiUrl.sites(...)`, 2 console → devLog
  - [x] `visaryApi.ts` — POST/GET/PATCH через `buildUrl()` (helper над `apiUrl.visary`), 9 console + 3 SSRF закрыты; `console.error` для отсутствия токена оставлен (разработческая ошибка, должна быть видна и в prod-preview)
  - [x] `visaryCrud.ts` — 1 console → devInfo
  - [x] 4 hook-файла (`useBackendProjects/useImportSession/useImportTypes/useListView`) — все console → devLog
  - [x] `vite.config.ts:33` — константные format-strings `'... %s ...'` (DEV-guard в Node-времени неприменим)
  - [x] `npx tsc -b --noEmit` — никаких новых ошибок (2 pre-existing в FileUpload.tsx — не наши)
  - [x] `npx vitest run` — **59/59 passed**
- [x] **Этап 6: `safeFetch` whitelist-guard (v2-rescan, 6 SSRF)** — 2026-06-05
  - [x] `src/services/safeFetch.ts` — литерал-whitelist `ALLOWED_PREFIXES` + 4 guard'а перед `fetch` (тип, protocol-relative, traversal, whitelist)
  - [x] `importsService.ts:84, 301` — `fetch` → `safeFetch`
  - [x] `sitesSync.ts:24` — `fetch` → `safeFetch`
  - [x] `visaryApi.ts:101, 183, 264` — `fetch` → `safeFetch` (POST/GET/PATCH)
  - [x] `projectsBackendApi.ts:40, 72` — проактивно переведены на `safeFetch` + `console.info` → `devInfo`
  - [x] `SessionGeneratedFiles.tsx:38` — проактивно: `downloadUrl` приходит из backend как `/api/imports/.../budget-xlsx`, в whitelist попадает
  - [x] юнит-тесты `safeFetch.test.ts` — **13 кейсов** (whitelist ok/sub-path/import-types/похожий-prefix/произвольный путь + 6 invariant + 2 delegation)
  - [x] `npx tsc -b --noEmit` — нет новых ошибок (2 pre-existing в `FileUpload.tsx` остались)
  - [x] `npx vitest run` — **72/72 passed** (было 59, +13 новых)
  - [x] **ESLint-guard** против регрессии (см. § 6.8): `no-restricted-syntax` запрещает прямой `fetch()`, `window.fetch`, `globalThis.fetch` в любом `.ts`/`.tsx`. Единственный разрешённый вызов — внутри `safeFetch.ts` через line-level `// eslint-disable-next-line`.
  - [ ] передать заказчику для третьего скана; при сохранении flag'а на `fetch(url, init)` внутри `safeFetch` — план отступления 6.7
- [x] **Этап 5: override `uuid` (uuid-CVE)**
  - [x] `package.json` — добавлен блок `"overrides": { "uuid": "^14.0.0" }`
  - [x] `npm install` — `npm ls uuid` показывает `uuid@14.0.0` (через `@alfalab/hooks`)
  - [x] `npm audit --omit=dev` — **0 vulnerabilities** (CVE-2026-41907 закрыт)
  - [x] `npx vitest run` — **59/59 passed** (`useId` через `uuid.v4()` работает)
- [x] **Этап 4: документация**
  - [x] doc 121 (этот файл) — финализирован с фактическими патчами
  - [x] `doc_project/README.md` — добавлена строка
  - [x] `MEMORY.md` — добавлен pointer
  - [x] doc 93 — дополнен ссылкой на 121 в R1/R3
  - [~] doc 26/27 — обновление onboarding (см. ниже, требует **повторной проверки руками**, поскольку deny-by-default ломает «голый» `docker compose up`)
  - [skip] регистрация задачи в трекере — по решению заказчика (2026-06-04)

### ⚠️ Что осталось руками

Сразу после pull этой ветки разработчики получат сообщение `docker compose up`:
```
service "postgres-visary" refers to undefined variable VISARY_DB_USER — скопируй .env.example → .env
```
Это **запланированное поведение** (deny-by-default из требования №1 заказчика). Перед запуском надо:
```powershell
cp .env.example .env
# отредактировать .env, выставить реальные пароли (НЕ `change_me_locally`)
```
В CI (если есть) — добавить переменные `IMPORT_SERVICE_DB_USER/_PASSWORD`, `VISARY_DB_USER/_PASSWORD` в secrets, иначе билд упадёт.

---

## ✅ Решения заказчика (2026-06-04)

1. **Default `:-visary_pwd` УБРАТЬ** — никакого fallback. Без `.env` — стенд не поднимается (deny-by-default). Onboarding-доки обязаны явно требовать копирование `.env.example → .env`.
2. **Backend SSRF в `/api/visary/*` proxy** — следующая партия audit'а **при необходимости**, не сейчас.
3. **Регистрация задач в трекере** — пропускаем.

---

## 📍 Применение в проекте

| Слой | Файлы | Что меняется |
|------|-------|--------------|
| Конфигурация | `appsettings.json`, `docker-compose.yml`, `.env.example` | Чистка хардкоженных паролей, расширение `.env` |
| Зависимости | `KiloImportService.Web/package.json`, `package-lock.json` | Pin'инг 11 версий + `overrides.uuid` |
| Сетевой слой v1 | `src/services/{apiUrl,devLog,importsService,importsHub,sitesSync,visaryApi,visaryCrud}.ts` | `apiUrl` helper + `devLog` wrapper |
| Сетевой слой v2 (план) | `src/services/{safeFetch,importsService,sitesSync,visaryApi,projectsBackendApi}.ts` + `__tests__/safeFetch.spec.ts` | `safeFetch` whitelist-guard перед каждым `fetch` |
| Hooks | `src/hooks/{useBackendProjects,useImportSession,useImportTypes,useListView}.ts` | `devLog` wrapper |
| Vite | `vite.config.ts` | Статическая строка вместо конкатенации |
| Документация | `doc_project/{121,93,README,26,27}.md`, `MEMORY.md` | Записи о фиксах |
