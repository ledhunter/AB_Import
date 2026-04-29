# 🔄 Интеграция UI ↔ собственный backend

## 📋 Описание

Полный контур взаимодействия React-UI с `KiloImportService.Api`: от загрузки
файла через REST до подписки на SignalR и отображения live-прогресса.
Архитектура — **трёхслойная** с явным разделением ответственности.

> 💡 SignalR-специфика и события прогресса описаны в [13-signalr-import-progress.md](./13-signalr-import-progress.md).
> Прокси `/api/imports` → backend — в [13-vite-proxy-backend.md](./13-vite-proxy-backend.md).
> Маппинг DTO ↔ UI-моделей — в этом же документе ниже.

---

## 🏗️ Архитектура слоёв

```
┌──────────────────────────────────────────────────────┐
│  UI: App.tsx                                         │
│       │                                              │
│       ▼                                              │
│  hooks/useImportSession  ← phase-machine             │
│       │   (SignalR + REST + AbortController)         │
│       ▼                                              │
│  services/importsService     services/importsHub     │
│       │ (REST-клиент)             │ (SignalR-обёртка)│
│       ▼                           ▼                  │
│  fetch('/api/imports/...')   /hubs/imports           │
│       │                           │                  │
│       └─────── Vite proxy ────────┘                  │
│                  │                                   │
│                  ▼                                   │
│  KiloImportService.Api (.NET 10 + EF Core + SignalR) │
└──────────────────────────────────────────────────────┘
```

| Слой | Файл | Ответственность |
|------|------|-----------------|
| Phase-state | `hooks/useImportSession.ts` | `idle/uploading/tracking/applying/completed/error` + cleanup |
| REST-клиент | `services/importsService.ts` | Логирование запросов, `ImportsApiError`, `AbortSignal` |
| SignalR-обёртка | `services/importsHub.ts` | `HubConnectionBuilder`, `JoinSession`, autoreconnect |
| Маппинг DTO → UI | `services/importMappers.ts` | `toUiSession`, `toUiReport`, `computeDuration` |
| API-DTO | `types/api.ts` | Точное отражение того, что отдаёт backend |
| UI-модели | `types/session.ts` | Презентационная модель + `SessionStatusVariant` |

---

## ✅ Правильная реализация

### Слой 1: REST-клиент (`importsService.ts`)

```ts
// Низкоуровневый fetch + единый формат ошибок + логирование с requestId.
let _requestCounter = 0;
const nextRequestId = () => (++_requestCounter).toString(36).padStart(4, '0');

async function fetchJson<T>(path: string, init: RequestInit & { signal?: AbortSignal }): Promise<T> {
  const id = nextRequestId();
  console.groupCollapsed(`[ImportsAPI] → ${init.method ?? 'GET'} ${path}  #${id}`);
  // ... performance.now() для длительности, AbortError → пробрасываем
  const response = await fetch(path, init);
  if (!response.ok) {
    // Парсим { error: "..." } из ответа, иначе текст response.statusText
    throw new ImportsApiError(serverMessage || `Backend ${response.status}`, response.status, raw);
  }
  return JSON.parse(raw);
}

export async function uploadImport(payload, options = {}) {
  const form = new FormData();
  form.set('importTypeCode', payload.importTypeCode);
  form.set('file', payload.file);
  if (payload.projectId != null) form.set('projectId', String(payload.projectId));
  if (payload.siteId != null) form.set('siteId', String(payload.siteId));
  return fetchJson<ApiUploadResult>('/api/imports', { method: 'POST', body: form, signal: options.signal });
}
```

**Ключевые решения:**
- **`FormData` для upload** — backend ждёт `[FromForm]`, не JSON.
- **`requestId`** в логах — связывает старт и финиш одного запроса (как в visaryApi.ts).
- **`ImportsApiError`** с `status` и `responseText` — UI может различать 401/403/500 без regex.
- **Возврат `undefined as T` при 204 No Content** — apply/cancel обычно ничего не отдают.

### Слой 2: SignalR-обёртка (`importsHub.ts`)

```ts
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';

export async function createImportsHub(handlers: ImportsHubHandlers = {}) {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/imports')          // 👈 относительный — Vite proxy перепишет
    .withAutomaticReconnect()           // 👈 0/2/10/30 сек по умолчанию
    .configureLogging(LogLevel.Warning) // 👈 не Info — иначе debug-спам
    .build();

  // Server → client события
  if (handlers.onStageStarted)   connection.on('StageStarted',   handlers.onStageStarted);
  if (handlers.onStageCompleted) connection.on('StageCompleted', handlers.onStageCompleted);
  if (handlers.onSessionStatus)  connection.on('SessionStatus',  handlers.onSessionStatus);
  if (handlers.onStageProgress)  connection.on('StageProgress',  handlers.onStageProgress);

  await connection.start();
  return {
    connection,
    joinSession: async (id: string) => {
      if (connection.state !== HubConnectionState.Connected) return;
      await connection.invoke('JoinSession', id);   // 👈 серверный метод
    },
    leaveSession: async (id: string) => {
      if (connection.state !== HubConnectionState.Connected) return;
      await connection.invoke('LeaveSession', id);
    },
    stop: async () => { /* безопасно идемпотентный */ },
  };
}
```

### Слой 3: Phase-machine (`useImportSession.ts`)

Главное состояние — `phase`, оно НЕ совпадает с `session.status`:

| `phase` | Когда | UI рендерит |
|---------|-------|-------------|
| `idle` | Сессии нет | Форма параметров |
| `uploading` | POST /api/imports летит | Кнопка с loading |
| `tracking` | sessionId получен, подписан на SignalR | `SessionProgress` (прогресс) |
| `applying` | POST /api/imports/{id}/apply | Disabled-кнопки + `SessionProgress` |
| `completed` | Status ∈ Applied / Failed / Cancelled | `SessionSummary` + `SessionRowsTable` |
| `error` | Ошибка до получения sessionId | Alert в форме |

```ts
export function useImportSession(): UseImportSessionState {
  const [phase, setPhase] = useState<ImportPhase>('idle');
  const [session, setSession] = useState<UiSession | null>(null);

  // Latest values в ref'ах — коллбэки SignalR не пересоздаются на ререндере.
  const sessionLatestRef = useRef<UiSession | null>(null);
  useEffect(() => { sessionLatestRef.current = session; }, [session]);  // 👈 запись в effect

  // Hub-подписки → setSession({ ...prev, ... }) с проверкой sessionId.
  const hub = await createImportsHub({
    onSessionStatus: (e) => {
      if (e.sessionId !== sessionIdRef.current) return;   // 👈 защита от старых событий
      const prev = sessionLatestRef.current;
      if (!prev) return;
      setSession({ ...prev, status: e.status, variant: toSessionVariant(e.status) });
      if (FINAL_STATUSES.has(e.status)) setPhase('completed');
      if (REPORT_LOAD_STATUSES.has(e.status)) void pullSession(e.sessionId);
    },
    onStageProgress: (e) => { /* обновляем session.stageProgress */ },
  });

  // Cleanup ОБЯЗАТЕЛЬНО — hub живёт дольше компонента.
  useEffect(() => () => {
    reportAbortRef.current?.abort();
    void hubRef.current?.stop();
    hubRef.current = null;
  }, []);
}
```

**Ключевые решения:**
- **REST-pull после SignalR-event** — для гарантии целостности (SignalR может
  потерять событие, REST-снимок всегда актуален).
- **`AbortController` на report-запросах** — при смене сессии или unmount
  запросы отменяются, без race conditions.
- **Запись `ref.current` в `useEffect`** — соблюдаем правило React 19 из
  [11-react-refs-discipline.md](./11-react-refs-discipline.md).

### Слой 4: маппер DTO → UI (`importMappers.ts`)

Backend отдаёт PascalCase-статусы и плоский DTO. UI хочет lowercase variants и
группировку ошибок по строкам.

```ts
export const toSessionVariant = (status: ApiImportStatus): SessionStatusVariant => {
  switch (status) {
    case 'Pending':                         return 'pending';
    case 'Parsing':
    case 'Validating':
    case 'Applying':                        return 'progress';
    case 'Validated':                       return 'awaiting';
    case 'Applied':                         return 'success';
    case 'Failed':                          return 'failed';
    case 'Cancelled':                       return 'cancelled';
  }
};

export function toUiReport(api: ApiImportReport, session: UiSession): UiReport {
  // Группируем ошибки по rowNumber → крепим к строкам.
  const errorsByRow = new Map<number, UiRowError[]>();
  const fileLevelErrors: UiRowError[] = [];
  for (const e of api.errors ?? []) {
    if (e.sourceRowNumber === 0) fileLevelErrors.push(toUiRowError(e));
    else errorsByRow.set(e.sourceRowNumber, [
      ...(errorsByRow.get(e.sourceRowNumber) ?? []),
      toUiRowError(e),
    ]);
  }

  const rows = (api.rows ?? []).map((r) => ({
    rowNumber: r.sourceRowNumber,
    status: r.status,
    errors: errorsByRow.get(r.sourceRowNumber) ?? [],
  }));

  // Осиротевшие ошибки (rowNumber > 0, но строки нет) → добавляем как Invalid.
  for (const [rowNumber, errors] of errorsByRow.entries()) {
    if (!rows.some((r) => r.rowNumber === rowNumber)) {
      rows.push({ rowNumber, status: 'Invalid', errors });
    }
  }
  rows.sort((a, b) => a.rowNumber - b.rowNumber);

  return { session: { ...session, status: api.status, ... }, rows, fileLevelErrors, rowsPagination };
}
```

---

## ❌ Типичные ошибки

### Ошибка 1: упрощение типов вместо двух слоёв

```ts
// НЕПРАВИЛЬНО — просто переиспользовать ApiImportSession в UI-компонентах
const SessionView = ({ session }: { session: ApiImportSession }) => {
  // session.status — PascalCase 'Validated', компонент должен сам switch'ать
};
```

**Почему плохо:** «PascalCase» статусы протекают в JSX, цветовая логика
дублируется в каждом компоненте, при изменении формата DTO ломается весь UI.

**Правильно:** API-DTO (`types/api.ts`) ↔ UI-модель (`types/session.ts`) —
маппер `toUiSession()` делает один раз, UI работает только с `UiSession.variant`.

### Ошибка 2: подписка на SignalR без проверки `sessionId`

```ts
// НЕПРАВИЛЬНО — обработчик принимает любые SessionStatus
onSessionStatus: (e) => {
  setSession({ ...session, status: e.status });   // ❌ пересечение с другой сессией
}
```

**Что произойдёт:** хаб-группа правильная, но если пользователь нажал
«Новый импорт» и стартовал новую сессию, события старой могут прийти позже
(reconnect, network jitter) → перетрут новый прогресс.

**Правильно:** `if (e.sessionId !== sessionIdRef.current) return;` в начале каждого хэндлера.

### Ошибка 3: cleanup без `hub.stop()`

```ts
// НЕПРАВИЛЬНО
useEffect(() => () => {
  // hub оставлен висеть — утечка соединений и обработчиков
}, []);
```

**Что произойдёт:** при unmount `App.tsx` (или горячей перезагрузке Vite) hub
продолжит подписку, появится дубль соединений → дубли событий → state-bugs.

**Правильно:** `useEffect(() => () => { void hubRef.current?.stop(); }, []);`.

### Ошибка 4: запись `ref.current` в фазе рендера

```ts
// НЕПРАВИЛЬНО — react-hooks/refs ругается
sessionLatestRef.current = session;   // ❌ во время render
return <SessionView session={session} />;
```

**Правильно:** `useEffect(() => { sessionLatestRef.current = session; }, [session]);`
(см. [11-react-refs-discipline.md](./11-react-refs-discipline.md)).

### Ошибка 5: чтение `import.meta.env.VITE_BACKEND_URL` в сервисе

```ts
// НЕПРАВИЛЬНО — ломает unit-тесты под Node
const BASE = import.meta.env.VITE_BACKEND_URL;
fetch(`${BASE}/api/imports`, ...);
```

**Правильно:** относительные пути `/api/imports` → Vite proxy перепишет.
Сервисы не должны знать про окружение.

---

## 🔁 State machine (life cycle)

```
                 ┌──────────────┐
                 │     idle     │  ← начальное состояние
                 └──────┬───────┘
                        │ start({ file, ... })
                        ▼
                 ┌──────────────┐
                 │  uploading   │  ← POST /api/imports
                 └──┬─────┬─────┘
                    │     │
              upload│     │error → ┌──────────┐
                fail │OK   │      │  error   │
                    │     │       └──────────┘
                    ▼
              ┌──────────────┐
       ┌─────►│   tracking   │  ← SignalR подключён
       │      └──┬─────┬─────┘
       │         │     │ apply()
       │         │     ▼
       │         │  ┌────────────┐
       │         │  │  applying  │
       │         │  └─────┬──────┘
       │         │        │
       │         │        ▼ (success | error из POST /apply)
       │         │  ┌─────────────┐
       │         └─►│  completed  │
       │            └─────┬───────┘
       │ reset()          │
       └──────────────────┘
```

---

## 📍 Применение в проекте

| Слой | Файл |
|------|------|
| App-state | `KiloImportService.Web/src/App.tsx` |
| Phase-machine | `KiloImportService.Web/src/hooks/useImportSession.ts` |
| Eager-load типов | `KiloImportService.Web/src/hooks/useImportTypes.ts` |
| REST-клиент | `KiloImportService.Web/src/services/importsService.ts` |
| SignalR | `KiloImportService.Web/src/services/importsHub.ts` |
| Маппер | `KiloImportService.Web/src/services/importMappers.ts` |
| API-DTO типы | `KiloImportService.Web/src/types/api.ts` |
| UI-модель типы | `KiloImportService.Web/src/types/session.ts` |
| Презентационные компоненты | `KiloImportService.Web/src/components/ImportSession/` |
| Тесты маппера | `KiloImportService.Web/src/services/__tests__/importMappers.test.ts` (21 кейс) |

---

## 🎯 Чек-лист при добавлении нового метода backend

- [ ] DTO добавлен в `types/api.ts` (PascalCase или camelCase — как сериализует backend)
- [ ] Метод добавлен в `services/importsService.ts` с поддержкой `AbortSignal`
- [ ] Если нужны новые статусы / поля — обновлены `types/session.ts` и `services/importMappers.ts`
- [ ] Если нужны live-обновления — добавлен SignalR-хэндлер в `importsHub.ts` + подписан
      в `useImportSession.ts` с проверкой `sessionId`
- [ ] Vite proxy в `vite.config.ts` пропускает новый префикс
- [ ] Unit-тесты на маппер (без фейковых fetch-ов — только trans­formации)
- [ ] При ошибке fetch — пользователь видит понятное сообщение через `ImportsApiError.message`
