# 🔌 Интеграция с Visary API (ListView)

## 📋 Описание

Прототип использует **реальный Visary API** для получения списка проектов строительства. Запросы идут через POST `/api/visary/listview/{mnemonic}` с Bearer-авторизацией.

---

## 🎯 Архитектура запросов

```
React (browser)
   ↓
useProjects() hook
   ↓
projectsService.fetchProjects()
   ↓
visaryPost('/listview/constructionproject', body)
   ↓
fetch('/api/visary/listview/constructionproject', { Authorization: Bearer ... })
   ↓
Vite dev-server proxy  ← обходит CORS
   ↓
https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionproject
   ↓
[ JSON response: { Data: [...], Total: 2387, Summaries: [] } ]
```

---

## ✅ Конфигурация

### 1. `.env.local` (локально, не коммитится)

```dotenv
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
VITE_VISARY_API_TOKEN=eyJhbGciOiJSUzI1NiIs...
```

**Шаблон лежит в `.env.example`** (коммитится). При установке:
1. `cp .env.example .env.local`
2. Подставить актуальный `VITE_VISARY_API_TOKEN`
3. Перезапустить `npm run dev`

> ⚠️ **Токен истекает** примерно через 1 час. При получении ошибки `401 Unauthorized`:
> - Зайти в Visary UI → DevTools → Network → любой запрос → скопировать `Authorization` header без префикса `Bearer `
> - Заменить значение в `.env.local`
> - Vite сам перезапустится при изменении `.env.local`

### 2. Vite proxy (`vite.config.ts`)

```ts
server: {
  proxy: {
    '/api/visary': {
      target: 'https://isup-alfa-test.k8s.npc.ba',
      changeOrigin: true,
      secure: true,
    },
  },
}
```

**Зачем**: браузер блокирует cross-origin запросы из `http://localhost:5173` к `https://isup-alfa-test.k8s.npc.ba` (CORS). Vite-proxy переписывает запросы на сервере и отдаёт ответ как same-origin.

> 📝 На production деплое прокси не нужен — backend будет на том же домене.

---

## ✅ Структура запроса (POST `/listview/constructionproject`)

### Заголовки

```
Content-Type: application/json
Authorization: Bearer <VITE_VISARY_API_TOKEN>
```

### Тело

```json
{
  "Mnemonic": "constructionproject",
  "PageSkip": 0,
  "PageSize": 50,
  "Columns": [
    "ID", "IdentifierKK", "IdentifierZPLM", "Title",
    "Type", "Phase", "Region", "Town", "Developer",
    "ProjectManagment", "Sponsor", "ProjectPeriod", "RowVersion"
  ],
  "Sorts": "[{\"selector\":\"ID\",\"desc\":true}]",
  "Hidden": false,
  "ExtraFilter": null,
  "SearchString": "",
  "AssociatedID": null
}
```

### Ответ (реальный формат, проверен опытным путём)

```json
{
  "Data": [
    {
      "ID": 123,
      "IdentifierKK": "KK-001",
      "IdentifierZPLM": "ZPLM-001",
      "Title": "ЖК Алые Паруса",
      "Type": "Жилой",
      "Phase": "Активный",
      ...
    }
  ],
  "Total": 2387,
  "Summaries": []
}
```

### ⚠️ Грабли: ключи ответа — `Data` / `Total`, а НЕ `Items` / `TotalCount`

При первой реализации я предположил формат `{ Items, TotalCount }` (по аналогии с другими списочными API) — **это было неверно**. UI получал HTTP 200 с реальными данными, но видел «получено 0 из 0 проектов», потому что искал не те ключи.

**Защитная реализация в `parseProjectsResponse`** поддерживает все варианты в порядке приоритета:

```ts
// src/services/projectsService.ts
export function parseProjectsResponse(
  raw: ListViewResponse<ConstructionProjectRaw>,
): { items: ProjectItem[]; totalCount: number } {
  const rows = raw.Data ?? raw.Items ?? raw.items ?? [];           // 👈 Data приоритетнее
  const items = rows.map(toProjectItem);
  const totalCount = raw.Total ?? raw.TotalCount ?? raw.totalCount ?? items.length;
  return { items, totalCount };
}
```

**Почему такой порядок:**
- `Data` / `Total` — реальный формат Visary (подтверждено логами `{Data: Array(50), Total: 2387, Summaries: Array(0)}`)
- `Items` / `TotalCount` — fallback на случай других эндпоинтов с PascalCase
- `items` / `totalCount` — fallback на случай camelCase-сериализации

> 📝 Это эталонный кейс **«никогда не доверяй своему предположению о формате — логируй ответ и смотри реальный JSON»**. Этот баг был пойман именно благодаря логированию `[VisaryAPI] ← 200 ... response: {Data: Array(50), Total: 2387, ...}` (см. раздел «Логирование» ниже).

---

## ✅ Применение в коде

### Слой 1: API-обёртка `src/services/visaryApi.ts`

```ts
export async function visaryPost<TResponse>(path: string, body: unknown): Promise<TResponse>;

export class VisaryApiError extends Error { ... }
export class VisaryAuthError extends VisaryApiError { ... }   // 401/403
```

**Ответственность:**
- Подставляет `Authorization: Bearer <token>` из `import.meta.env`
- Префиксует путь `/api/visary` (попадёт в Vite proxy)
- Бросает `VisaryAuthError` при 401/403 с понятным сообщением
- Логирует HTTP-статус в `VisaryApiError.status`

### Слой 2: Сервис `src/services/projectsService.ts`

```ts
export async function fetchProjects(options?: {
  pageSkip?: number;
  pageSize?: number;
  searchString?: string;
  signal?: AbortSignal;
}): Promise<{ items: ProjectItem[]; totalCount: number }>;

export function toProjectItem(raw: ConstructionProjectRaw): ProjectItem;
```

**Ответственность:**
- Знает PascalCase-схему запроса/ответа Visary
- Маппит `ConstructionProjectRaw` (PascalCase) → `ProjectItem` (camelCase, UI-формат)
- Fallback'и для пустых полей: `code = IdentifierKK || IdentifierZPLM || 'ID-{id}'`

### Слой 3: Хук `src/hooks/useProjects.ts` (lazy-load)

```ts
export type ProjectsStatus = 'idle' | 'loading' | 'success' | 'error';

export function useProjects(): {
  data: ProjectItem[];
  status: ProjectsStatus;
  error: string | null;
  totalCount: number;
  load: () => void;       // ленивая загрузка (вызвать при первом open Select)
  refetch: () => void;    // принудительный перезапрос
};
```

**Ответственность:**
- Запрос **не идёт** автоматически при монтировании — только по `load()`
- `load()` идемпотентный: повторный вызов в `loading` или `success` игнорируется
- `refetch()` — принудительный перезапрос (например, после ошибки)
- При активном запросе и новом `run()` старый отменяется через `AbortController.abort()`
- Все переходы статуса логируются в консоль

> 📝 Подробнее про lazy-load паттерн см. отдельный документ [09-lazy-loaded-select.md](./09-lazy-loaded-select.md).

### Слой 4: UI `ImportForm.tsx`

```tsx
const { data: projects, status, error, load, refetch } = useProjects();

// Lazy-load: запрашиваем при первом открытии Select
const handleProjectsOpen = (payload: { open?: boolean }) => {
  if (payload.open) load();
};

const placeholder =
  status === 'loading' ? 'Загрузка проектов…' :
  status === 'error'   ? 'Ошибка загрузки проектов' :
  status === 'success' ? (projects.length === 0 ? 'Проекты не найдены' : 'Выберите проект') :
                          'Нажмите для загрузки проектов';

<Select
  label="Проект"
  placeholder={placeholder}
  options={projects.map(p => ({ key: String(p.id), content: `${p.title} (${p.code})` }))}
  selected={projectId !== null ? String(projectId) : null}    /* 👈 не undefined! */
  onOpen={handleProjectsOpen}
  disabled={status === 'loading'}
/>
{status === 'error' && error && (
  <>
    <Typography.Text color="negative">{error}</Typography.Text>
    <button onClick={refetch}>Повторить</button>
  </>
)}
```

---

## ❌ Типичные ошибки

### Ошибка 1: токен в коде

```ts
// НЕПРАВИЛЬНО
const TOKEN = 'eyJhbGciOi...';
```

**Почему плохо:**
- Токен попадёт в git
- При истечении придётся править код, а не просто .env.local
- Невозможно дать разным разработчикам разные токены

**Правильно:** `.env.local` (в .gitignore) + `import.meta.env.VITE_VISARY_API_TOKEN`.

### Ошибка 2: запросы напрямую без proxy

```ts
// НЕПРАВИЛЬНО — упадёт на CORS из dev-сервера
fetch('https://isup-alfa-test.k8s.npc.ba/api/visary/listview/...');
```

**Правильно:** относительный путь `/api/visary/...`, который прокинет Vite.

### Ошибка 3: чтение токена при импорте модуля

```ts
// НЕПРАВИЛЬНО — ломает unit-тесты под Node
const TOKEN = import.meta.env.VITE_VISARY_API_TOKEN;
```

**Почему плохо:**
- В Node `import.meta.env` undefined → `TypeError: Cannot read 'VITE_VISARY_API_TOKEN' of undefined`
- Тесты ломаются при простом импорте сервиса

**Правильно:** ленивое чтение в функции:
```ts
function getToken(): string {
  const env = (import.meta as { env?: Record<string, string | undefined> }).env;
  return env?.VITE_VISARY_API_TOKEN ?? '';
}
```

### Ошибка 4: `??` вместо `||` для fallback'ов с пустыми строками

```ts
// БАГ — пустая строка пропустится
title: raw.Title ?? `Проект #${id}`;   // raw.Title === '' → ''
```

**Правильно:**
```ts
title: raw.Title || `Проект #${id}`;   // '' || X → X
```

> 📝 Этот баг был найден unit-тестом `toProjectItem` — отличный пример пользы тестов.

### Ошибка 5: блокировка UI при загрузке

```tsx
// НЕПРАВИЛЬНО — UI «зависает» с пустым Select
{loading ? <Spinner /> : <Select options={projects} />}
```

**Правильно:** `Select` рендерится сразу с `disabled={loading}` и плейсхолдером «Загрузка…» — UI не «прыгает», состояние явное.

---

## 📊 Логирование (debug-стек)

В прототипе реализовано **трёхслойное логирование** запросов — это сильно ускоряет диагностику проблем (например, баг с `Items` vs `Data` был пойман именно так).

### Слой 1: Browser console (`visaryApi.ts`)

```
▶ [VisaryAPI] → POST /api/visary/listview/constructionproject  #ab12cd
    token: eyJhbGciOiJS...mfPvHzA (len=1123)
    request body: {Mnemonic: 'constructionproject', PageSkip: 0, PageSize: 50, ...}
▶ [VisaryAPI] ← 200 /api/visary/listview/constructionproject #ab12cd (177ms)
    response: {Data: Array(50), Total: 2387, Summaries: Array(0)}
```

**Что в логе:**
- Уникальный `requestId` (6 символов) — связывает старт и финиш одного запроса
- Маска токена `eyJhbGciOiJS...mfPvHzA (len=1123)` — видно что токен не пустой, без утечки полного значения
- Полное тело запроса
- Длительность в `ms`
- На ошибке — статус-код, тело ошибки, тип ошибки (`Auth`/`Network`/`API`)

### Слой 2: React-хук (`useProjects.ts`)

```
[ImportForm] Select "Проект" onOpen — open=true, status=idle
[useProjects] load() вызван (текущий status: idle)
[useProjects] status: idle/error → loading
... (запрос) ...
[useProjects] ✓ status: loading → success | получено 50 из 2387 проектов
[useProjects] первые 3 проекта: [{id: 1, title: 'ЖК А', code: 'KK-1', raw: {...}}, ...]
```

**Что в логе:**
- Каждое открытие Select с текущим `status`
- Вызовы `load()` / `refetch()` (включая случаи когда они **пропущены** из-за идемпотентности)
- Переходы статуса `idle → loading → success/error`
- Краткое превью первых 3 элементов (структура проверки маппинга `raw → ProjectItem`)

### Слой 3: Vite dev-server (терминал, где `npm run dev`)

```
[Vite proxy] → POST https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionproject
[Vite proxy] ← 200 POST /api/visary/listview/constructionproject
```

**Что в логе:**
- Полный URL цели на этапе `proxyReq` — видно что проксируется именно в production-домен
- HTTP статус ответа от сервера (на этапе `proxyRes`)
- Сетевые ошибки (DNS, TLS, ECONNREFUSED) — на этапе `error`

### Реализация

```ts
// vite.config.ts
proxy: {
  '/api/visary': {
    target: visaryTarget,
    changeOrigin: true,
    secure: true,
    configure: (proxy) => {
      proxy.on('proxyReq', (_proxyReq, req) => {
        console.log(`[Vite proxy] → ${req.method} ${visaryTarget}${req.url}`);
      });
      proxy.on('proxyRes', (proxyRes, req) => {
        console.log(`[Vite proxy] ← ${proxyRes.statusCode} ${req.method} ${req.url}`);
      });
      proxy.on('error', (err, req) => {
        console.error(`[Vite proxy] ✗ ERROR ${req.method} ${req.url} —`, err.message);
      });
    },
  },
}
```

### ⚠️ Важно

- **Логи маскируют токен** в браузере (только префикс/суффикс + длина). Не показывайте Console на скриншотах с реальными токенами всё равно.
- **`console.groupCollapsed`** для запроса/ответа — раскрывается только при необходимости, не засоряет Console
- **`performance.now()`** — точное измерение длительности (не `Date.now()`)
- При **AbortError** (отменённый запрос) логируется `[VisaryAPI] ⊘ aborted`, не как ошибка

### Чек-лист «как диагностировать API-проблему»

1. Открыть DevTools → **Console** — есть ли запрос?
   - Нет? → проблема в UI (Select.onOpen не сработал или `load()` пропустился из-за `status`)
   - Да → идти к шагу 2
2. Найти лог `[VisaryAPI] → POST ...` — указан ли токен и тело?
   - Токен пустой → проблема в `.env.local`
   - Тело неправильное → проблема в `projectsService.fetchProjects()`
3. Найти лог `[VisaryAPI] ← {status} ...` — какой статус и сколько мс?
   - Статус 401/403 → токен истёк
   - Статус 4xx → неправильное тело запроса (см. response.body в логе ошибки)
   - Статус 5xx → проблема на стороне Visary
   - Очень долго (>5s) → сетевая проблема
4. Если статус 200, но UI пустой → смотреть `response: {...}` в логе
   - Структура не совпадает с ожиданием → обновить `parseProjectsResponse` (как было с `Data`/`Total`)
5. Параллельно смотреть терминал Vite → есть ли `[Vite proxy] → ... ← ...` пара?
   - Только → без ← → запрос ушёл на сервер, но ответ не пришёл (timeout/network)
   - Есть `[Vite proxy] ✗ ERROR ...` → сетевая ошибка (DNS, TLS, недоступность)

---

## 🧪 Тесты

`src/services/__tests__/projectsService.test.ts` — 10 кейсов:

**`toProjectItem` (5 кейсов):**
- Title + IdentifierKK заполнены
- Пустой IdentifierKK → fallback на IdentifierZPLM
- Оба идентификатора пустые → `ID-{id}`
- Пустой Title → `Проект #{id}` (поймал реальный баг с `??` vs `||`)
- Полностью неполный JSON → fallback'и работают

**`parseProjectsResponse` (5 кейсов):**
- Реальный формат Visary `{ Data, Total, Summaries }` (фиксирует контракт после баг-фикса)
- Fallback `{ Items, TotalCount }`
- Fallback camelCase `{ items, totalCount }`
- Пустой ответ `{}` → `[]` + `0`
- Приоритет: `Data` важнее `Items`, `Total` важнее `TotalCount`

**Запуск:** `npx tsx src/services/__tests__/projectsService.test.ts`

> 📝 `__tests__` исключён из `tsconfig.app.json` (использует Node API). Тесты проверяют только маппинг, реальный API проверяется руками через UI.

---

## 📍 Применение в проекте

| Файл | Назначение |
|------|------------|
| `KiloImportService.Web/.env.local` | Токен (gitignored) |
| `KiloImportService.Web/.env.example` | Шаблон (коммитится) |
| `KiloImportService.Web/vite.config.ts` | Proxy `/api/visary` |
| `KiloImportService.Web/src/types/listView.ts` | `ListViewRequest`, `ListViewResponse<T>`, `ConstructionProjectRaw`, `ProjectItem` |
| `KiloImportService.Web/src/services/visaryApi.ts` | `visaryPost`, `VisaryApiError`, `VisaryAuthError` |
| `KiloImportService.Web/src/services/projectsService.ts` | `fetchProjects`, `toProjectItem` |
| `KiloImportService.Web/src/hooks/useProjects.ts` | Хук с loading/error |
| `KiloImportService.Web/src/components/ImportForm/ImportForm.tsx` | Использует `useProjects()` |
| `KiloImportService.Web/src/services/__tests__/projectsService.test.ts` | Unit-тесты маппера |

---

## 🎯 Чек-лист для добавления нового ListView-эндпоинта

> 📝 С версии «библиотека ListView» (см. [10-listview-library.md](./10-listview-library.md))
> сетевая часть и парсинг ответа вынесены в generic-ядро. Не реализуй `fetchXxx`,
> `parseXxxResponse` и `useXxx` руками — используй фабрики.

- [ ] Описать сырой тип `XxxRaw` в `types/listView.ts` (PascalCase, как в API)
- [ ] Описать UI-тип `XxxItem` (camelCase, нормализованный)
- [ ] Создать `src/services/listView/entities/xxx.ts`:
  - Константа `XXX_COLUMNS` со списком запрашиваемых колонок
  - Маппер `toXxxItem(raw)` через `||` (не `??`)
  - `xxxService = createListViewService({ mnemonic, columns, toItem, logTag })`
- [ ] Создать хук-обёртку `hooks/useXxx.ts` через `useListView(xxxService)`
- [ ] Использовать в UI с `loading`/`error` состояниями (контракт прежний)
- [ ] Написать unit-тесты на `toXxxItem` в `services/listView/__tests__/xxx.test.ts`
- [ ] Запустить тесты: `npx tsx src/services/listView/__tests__/xxx.test.ts`
