# 🎯 Lazy-loaded Select (загрузка опций по клику)

## 📋 Описание

Вместо загрузки списка опций при монтировании компонента (eager) — запрос идёт **только при первом открытии Select** (lazy). Это паттерн «pay for what you use» из мира веб-производительности.

**Когда использовать:**
- Список **большой** (десятки/сотни элементов из API)
- Список **дорогой** (медленный backend, тяжёлый payload)
- Пользователь **может не открыть** этот Select вообще
- Нужно избежать запроса при `mount` страницы (быстрее initial render)

**Когда НЕ использовать:**
- Малый статический список → сразу в `useMemo`
- Поле обязательное и сразу видно при загрузке формы → eager быстрее по UX

---

## ✅ Правильная реализация

### Слой 1: Хук с состояниями `idle / loading / success / error`

```ts
// src/hooks/useProjects.ts
import { useCallback, useRef, useState } from 'react';

export type ProjectsStatus = 'idle' | 'loading' | 'success' | 'error';

export function useProjects() {
  const [data, setData] = useState<ProjectItem[]>([]);
  const [status, setStatus] = useState<ProjectsStatus>('idle');   // 👈 начальное состояние idle, не loading
  const [error, setError] = useState<string | null>(null);
  const [totalCount, setTotalCount] = useState(0);
  const inFlightRef = useRef<AbortController | null>(null);

  const run = useCallback(() => {
    inFlightRef.current?.abort();                                  // 👈 отмена предыдущего запроса
    const controller = new AbortController();
    inFlightRef.current = controller;

    setStatus('loading');
    setError(null);

    fetchProjects({ signal: controller.signal })
      .then((res) => {
        if (controller.signal.aborted) return;
        setData(res.items);
        setTotalCount(res.totalCount);
        setStatus('success');
      })
      .catch((err) => {
        if (err?.name === 'AbortError') return;
        setError(err instanceof Error ? err.message : String(err));
        setStatus('error');
      });
  }, []);

  // Идемпотентный load — пропускает вызов, если уже loading/success
  const load = useCallback(() => {
    if (status === 'loading' || status === 'success') return;     // 👈 ключевая защита
    run();
  }, [status, run]);

  // Принудительный перезапуск (после error или для refresh)
  const refetch = useCallback(() => run(), [run]);

  return { data, status, error, totalCount, load, refetch };
}
```

### Слой 2: UI вызывает `load()` при `onOpen` Select

```tsx
// src/components/ImportForm/ImportForm.tsx
const { data: projects, status, error, load, refetch } = useProjects();

const handleProjectsOpen = (payload: { open?: boolean }) => {
  if (payload.open) load();                                        // 👈 только при открытии, не закрытии
};

const placeholder =
  status === 'loading' ? 'Загрузка проектов…' :
  status === 'error'   ? 'Ошибка загрузки проектов' :
  status === 'success' ? (projects.length === 0 ? 'Проекты не найдены' : 'Выберите проект') :
                          'Нажмите для загрузки проектов';         // 👈 idle: явная подсказка пользователю

<Select
  label="Проект"
  placeholder={placeholder}
  options={projects.map(p => ({ key: String(p.id), content: `${p.title} (${p.code})` }))}
  selected={projectId !== null ? String(projectId) : null}         /* 👈 null, не undefined! */
  onOpen={handleProjectsOpen}
  disabled={status === 'loading'}
  block
/>

{status === 'error' && error && (
  <>
    <Typography.Text color="negative">{error}</Typography.Text>
    <button onClick={refetch}>Повторить</button>
  </>
)}
```

### ⚠️ Важно

- **Начальный статус — `idle`**, не `loading`. Это позволяет показать осмысленный плейсхолдер «Нажмите для загрузки» вместо мигающей «Загрузка…»
- **`load()` идемпотентный** — повторное открытие Select не дёргает API. Для refresh — `refetch()`
- **`AbortController` отменяет предыдущий запрос** при новом `run()` — например, если пользователь быстро открыл-закрыл-открыл Select
- **`selected={null}`, не `undefined`** — иначе Downshift выкинет warning о смене controlled/uncontrolled (см. [01-alfa-core-components-api.md](./01-alfa-core-components-api.md))
- **Кнопка «Повторить»** на ошибке — для пользователей часто это единственный способ восстановиться (особенно после 401 при истёкшем токене)

---

## 🔁 State machine

```
              ┌─────────────────────────────────┐
              │                                 │
              │            ┌──→ success ───┐    │
              │            │               │    │
   idle ──load()──→ loading                ├──refetch()──┐
              │            │               │    │        │
              │            └──→ error  ────┘    │        │
              │                   │             │        │
              └───────────────────┴─────────────┘        │
                                  │                      │
                                  └──refetch()───────────┘
```

| Текущий статус | `load()` | `refetch()` |
|----------------|----------|-------------|
| `idle` | → `loading` | → `loading` |
| `loading` | пропуск | старый abort + новый запрос |
| `success` | пропуск (данные уже есть) | → `loading` (свежие данные) |
| `error` | → `loading` (повтор) | → `loading` |

---

## ❌ Типичные ошибки

### Ошибка 1: запрос в `useEffect([])` при mount

```tsx
// НЕПРАВИЛЬНО — eager loading
useEffect(() => {
  fetchProjects().then(setData);
}, []);
```

**Почему плохо:**
- Запрос идёт всегда, даже если пользователь не откроет Select
- При большом списке (2387 проектов в нашем случае) — лишний трафик
- Замедляет initial render страницы

### Ошибка 2: запрос на каждое открытие Select

```tsx
// НЕПРАВИЛЬНО — re-fetch при каждом open
const handleOpen = (payload) => {
  if (payload.open) fetchProjects().then(setData);   // ❌ нет проверки текущего статуса
};
```

**Почему плохо:**
- Пользователь открыл, посмотрел, закрыл, открыл снова → второй запрос не нужен
- Пользователь видит мигающую «Загрузка…» хотя данные уже есть
- Лишняя нагрузка на API

**Правильно:** идемпотентный `load()` с проверкой `status === 'loading' || status === 'success'`.

### Ошибка 3: `loading` как начальное состояние

```ts
// НЕПРАВИЛЬНО
const [status, setStatus] = useState<Status>('loading');   // ❌ при mount показывается «Загрузка…»
```

**Почему плохо:**
- Пользователь видит «Загрузка проектов…» хотя никакого запроса ещё нет
- Это вводит в заблуждение — кажется, что что-то застряло

**Правильно:** `'idle'` + плейсхолдер «Нажмите для загрузки» — явный сигнал «жду действия».

### Ошибка 4: `selected={undefined}` в controlled Select

```tsx
// НЕПРАВИЛЬНО — Downshift warning
selected={projectId ? String(projectId) : undefined}
```

**Почему плохо:** Downshift трактует `undefined` как «uncontrolled mode». При первом значении переключится на controlled → warning «changed uncontrolled to controlled».

**Правильно:** `selected={projectId !== null ? String(projectId) : null}`

### Ошибка 5: нет отмены запроса при unmount или повторе

```ts
// НЕПРАВИЛЬНО — race condition
const load = () => {
  fetchProjects().then(setData);   // ❌ если пользователь быстро дважды кликнет, оба запроса завершатся
};
```

**Почему плохо:**
- Пользователь открыл → load → закрыл → открыл (для refetch) → load
- Старый ответ может прийти **после** нового → перетрёт свежие данные
- При unmount компонента старый запрос всё ещё идёт → утечка

**Правильно:** `AbortController` + флаг `signal.aborted` в обработчике.

### Ошибка 6: `??` вместо `||` для fallback

```ts
// БАГ — поймал unit-тест
title: raw.Title ?? `Проект #${raw.ID}`;   // raw.Title === '' → '' (пустая строка != null/undefined)
```

**Почему плохо:** `??` срабатывает только на `null`/`undefined`. Если backend вернёт пустую строку — она пройдёт.

**Правильно:** `title: raw.Title || 'Проект #${raw.ID}'`

---

## 🔍 Диагностика

С реализованным [многослойным логированием](./08-visary-api-integration.md#-логирование-debug-стек) видно всю цепочку:

### Сценарий 1: первый клик на Select

```
[ImportForm] Select "Проект" onOpen — open=true, status=idle
[useProjects] load() вызван (текущий status: idle)
[useProjects] status: idle/error → loading
[VisaryAPI] → POST /api/visary/listview/constructionproject  #ab12cd
[Vite proxy] → POST https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionproject
[Vite proxy] ← 200 POST /api/visary/listview/constructionproject
[VisaryAPI] ← 200 /api/visary/listview/constructionproject #ab12cd (177ms)
[useProjects] ✓ status: loading → success | получено 50 из 2387 проектов
```

### Сценарий 2: повторное открытие (данные уже есть)

```
[ImportForm] Select "Проект" onOpen — open=true, status=success
[useProjects] load() пропущен — данные уже загружены (50 проектов). Используй refetch() для перезагрузки.
```

Запрос **не идёт** — `load()` идемпотентный.

### Сценарий 3: ошибка

```
[ImportForm] Select "Проект" onOpen — open=true, status=idle
[useProjects] load() вызван (текущий status: idle)
[VisaryAPI] ✗ 401 Unauthorized #ab12cd (203ms) {error: '...'}
[useProjects] ✗ status: loading → error | Bearer-токен Visary истёк или невалиден...
```

UI показывает плейсхолдер «Ошибка загрузки проектов» + кнопку «Повторить» (вызовет `refetch()`).

---

## 📍 Применение в проекте

| Слой | Файл | Описание |
|------|------|----------|
| Хук | `KiloImportService.Web/src/hooks/useProjects.ts` | `useProjects() → { data, status, error, totalCount, load, refetch }` |
| UI | `KiloImportService.Web/src/components/ImportForm/ImportForm.tsx` | `onOpen={handleProjectsOpen}` → `load()` при `payload.open` |
| API | `KiloImportService.Web/src/services/projectsService.ts` | `fetchProjects({ signal })` принимает AbortSignal |
| API-обёртка | `KiloImportService.Web/src/services/visaryApi.ts` | `visaryPost(path, body, { signal })` пробрасывает signal в fetch |

---

## 🎯 Чек-лист для нового lazy-Select

- [ ] Хук возвращает `{ data, status, error, load, refetch }` (status включает `idle`)
- [ ] Начальное `status === 'idle'`, **не** `loading`
- [ ] `load()` пропускает вызов при `status === 'loading' || 'success'`
- [ ] `refetch()` всегда стартует новый запрос (не идемпотентный)
- [ ] Используется `AbortController`, старый запрос отменяется при новом `run()`
- [ ] `Select` имеет `onOpen={p => p.open && load()}`
- [ ] `Select.selected` всегда `string | OptionShape | null`, **не** `undefined`
- [ ] `Select.disabled` отражает `status === 'loading'`
- [ ] Плейсхолдер меняется по статусу (4 варианта: idle/loading/error/success-empty)
- [ ] При `error` — текст ошибки + кнопка «Повторить» с `refetch()`
- [ ] Логи на каждый переход статуса и пропуск `load()`
- [ ] Unit-тесты на парсинг ответа и маппинг raw → UI-тип
