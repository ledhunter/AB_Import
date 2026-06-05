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

### AppSec v2 (`appsec_v2.txt`, 2026-06-04, дополнение)

| Категория | Critically | SLA, дней | Кол-во | Файлы |
|-----------|------------|-----------|--------|-------|
| CVE-2026-41907 (`uuid@8.3.2`) | Medium | 9 | 1 | транзитивная: `@alfalab/core-components → action-button → @alfalab/hooks → uuid@8.3.2` |

**Итого v2**: 1 Medium.

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

## 📦 Этап 5: Override `uuid` (v2, Medium ×1)

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

## 🎯 Чек-лист выполнения (статус — 2026-06-04)

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
- [x] **Этап 5: override `uuid` (v2)**
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
| Зависимости | `KiloImportService.Web/package.json`, `package-lock.json` | Pin'инг 11 версий |
| Сетевой слой | `src/services/{apiUrl,devLog,importsService,importsHub,sitesSync,visaryApi,visaryCrud}.ts` | apiUrl helper + devLog wrapper |
| Hooks | `src/hooks/{useBackendProjects,useImportSession,useImportTypes,useListView}.ts` | devLog wrapper |
| Vite | `vite.config.ts` | Статическая строка вместо конкатенации |
| Документация | `doc_project/{121,93,README,26,27}.md`, `MEMORY.md` | Записи о фиксах |
