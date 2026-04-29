# 📡 SignalR прогресс импорта

## 📋 Описание

Сервер шлёт UI **четыре события** через SignalR-хаб `/hubs/imports` для real-time
отслеживания импорта. UI подписывается через `JoinSession(sessionId)` и слушает
сообщения только своей сессии.

> 🔌 Vite proxy для SignalR (с `ws: true`) — см. [13-vite-proxy-backend.md](./13-vite-proxy-backend.md).
> Полный контур интеграции — см. [14-imports-backend-integration.md](./14-imports-backend-integration.md).

---

## 📨 Контракт событий

| Событие | Когда | Payload |
|---------|-------|---------|
| `SessionStatus` | Любая смена статуса сессии | `{ sessionId, status: ApiImportStatus }` |
| `StageStarted` | Старт новой стадии (Parse/Validate/Apply) | `{ sessionId, stage: 'Parse' \| 'Validate' \| 'Apply' }` |
| `StageProgress` | Прогресс по строкам внутри стадии (≈50 апдейтов на файл) | `{ sessionId, stage, currentRow, totalRows, percentComplete, sheet }` |
| `StageCompleted` | Завершение стадии | `{ sessionId, stage, rows?, validRows?, invalidRows?, applied? }` |

**Server-side методы хаба** (вызывает клиент через `connection.invoke`):
- `JoinSession(sessionId: string)` — присоединиться к группе `session:{id}`.
- `LeaveSession(sessionId: string)` — выйти из группы.

**Группа** на сервере: `"session:" + sessionId`. Все события одной сессии
рассылаются только в эту группу — другие пользователи не увидят чужой прогресс.

---

## ✅ Правильная реализация

### Backend: публикация StageProgress (троттлинг)

```csharp
// KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs (Validate-этап)
var totalRowsValidate = validation.Rows.Count;
// ≈ 50 апдейтов на файл — компромисс между плавностью UI и нагрузкой на хаб.
var notifyEvery = Math.Max(1, totalRowsValidate / 50);

for (int i = 0; i < validation.Rows.Count; i++)
{
    ct.ThrowIfCancellationRequested();
    var mr = validation.Rows[i];
    var raw = parseResult.Rows[i];
    // ... сохранение StagedRow + ImportError ...

    var processed = i + 1;
    if (processed == totalRowsValidate || processed % notifyEvery == 0)
    {
        var percent = (int)Math.Round((processed * 100.0) / totalRowsValidate);
        validateStage.ProgressPercent = percent;
        await _hub.Clients.Group(groupName).SendAsync("StageProgress", new
        {
            sessionId,
            stage = "Validate",
            currentRow = processed,
            totalRows = totalRowsValidate,
            percentComplete = percent,
            sheet = raw.Sheet,           // 👈 имя листа Excel — UI покажет
        }, ct);
    }
}
```

### ⚠️ Важно про троттлинг

- **Не шли событие на каждой строке.** Файл с 10 000 строк → 10 000 SignalR-сообщений
  → DDoS WebSocket-канала, зависание UI.
- **`Math.Max(1, total / 50)`** — на маленьких файлах (50-100 строк) шлёт каждую
  итерацию, на больших — каждую N-ую. UI получает плавную картинку.
- **Последняя итерация шлётся всегда** (`processed == totalRowsValidate`) — чтобы
  100% точно дошло.

### UI: подписка с проверкой sessionId

```ts
// services/importsHub.ts
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';

export async function createImportsHub(handlers: ImportsHubHandlers = {}) {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/imports')
    .withAutomaticReconnect()           // 👈 0/2/10/30 сек по умолчанию
    .configureLogging(LogLevel.Warning) // 👈 не Info — иначе debug-спам
    .build();

  if (handlers.onStageProgress) {
    connection.on('StageProgress', (e: StageProgressEvent) => {
      // StageProgress приходит ~50 раз — лог в debug-уровне.
      console.debug('[ImportsHub] ← StageProgress', e);
      handlers.onStageProgress?.(e);
    });
  }
  // ... onStageStarted, onStageCompleted, onSessionStatus аналогично ...

  await connection.start();

  return {
    connection,
    joinSession: async (id: string) => {
      if (connection.state !== HubConnectionState.Connected) return;
      await connection.invoke('JoinSession', id);
    },
    leaveSession: async (id: string) => { /* симметрично */ },
    stop: async () => { /* идемпотентный */ },
  };
}
```

### UI: применение прогресса в state

```ts
// hooks/useImportSession.ts
const hub = await createImportsHub({
  onStageProgress: (e) => {
    // Гарда от событий старой сессии после reset()
    if (e.sessionId !== sessionIdRef.current) return;
    const prev = sessionLatestRef.current;
    if (!prev) return;
    setSession({
      ...prev,
      stageProgress: {
        stage: e.stage,
        currentRow: e.currentRow,
        totalRows: e.totalRows,
        percentComplete: e.percentComplete,
        sheet: e.sheet ?? null,
      },
    });
  },
  onStageCompleted: (e) => {
    if (e.sessionId !== sessionIdRef.current) return;
    // Очищаем live-прогресс при завершении стадии — следующая стадия
    // запустится с чистым счётчиком.
    const prev = sessionLatestRef.current;
    if (prev?.stageProgress) {
      setSession({ ...prev, stageProgress: null });
    }
  },
});
```

### UI: отображение в `SessionProgress`

```tsx
const live = session.stageProgress;

return (
  <>
    <ProgressBar value={percent} view="accent" size={8} />
    {live && live.totalRows > 0 && (
      <Typography.Text view="primary-small" color="secondary" tag="div">
        {STAGE_LABELS[live.stage]}: строка {live.currentRow} из {live.totalRows}
        {live.sheet ? ` (лист «${live.sheet}»)` : ''} · {live.percentComplete}%
      </Typography.Text>
    )}
  </>
);
```

### Расчёт общего процента (`computePercent`)

```ts
const ranges = {
  Upload:   [0,   5],
  Parse:    [5,   40],
  Validate: [40,  80],
  Apply:    [80, 100],
};

// Каждая завершённая стадия даёт от и до.
// Live-прогресс по строкам перекрывает stage.progressPercent внутри текущей стадии.
if (session.stageProgress) {
  const [from, to] = ranges[session.stageProgress.stage];
  percent = Math.max(percent, from + ((to - from) * session.stageProgress.percentComplete) / 100);
}
```

---

## ❌ Типичные ошибки

### Ошибка 1: SendAsync на каждую строку

```csharp
// НЕПРАВИЛЬНО — в цикле без троттлинга
for (int i = 0; i < rows.Count; i++) {
    await _hub.Clients.Group(g).SendAsync("StageProgress", new { ... }, ct);
}
```

**Что произойдёт:** на файле в 10k строк отправится 10k SignalR-сообщений за
несколько секунд. WebSocket-канал захлёбывается, UI становится unresponsive.

**Правильно:** `if (processed % notifyEvery == 0)` с `notifyEvery = total / 50`.

### Ошибка 2: SignalR без `withAutomaticReconnect`

```ts
// НЕПРАВИЛЬНО — соединение не восстановится после network jitter
const connection = new HubConnectionBuilder()
  .withUrl('/hubs/imports')
  .build();
```

**Что произойдёт:** на 30-секундном wifi-сбое UI потеряет события навсегда —
будет показывать 47% до посмерти страницы. Пользователю кажется «зависло».

**Правильно:** `.withAutomaticReconnect()` — встроенный backoff `0/2/10/30 сек`.
После reconnect нужно повторно вызвать `joinSession(id)` (наша обёртка делает
это самостоятельно через `onreconnected`).

### Ошибка 3: подписка без проверки `sessionId`

```ts
// НЕПРАВИЛЬНО
onStageProgress: (e) => setSession({ ...session, stageProgress: e })
```

**Что произойдёт:** пользователь нажал «Новый импорт» (`reset()`), стартовал
новую сессию. Но событие старой сессии может прийти позже из reconnect-буфера —
перетрёт прогресс новой.

**Правильно:** `if (e.sessionId !== sessionIdRef.current) return;` в начале
каждого хэндлера.

### Ошибка 4: чтение `session.stageProgress` в render с предположением, что оно есть

```tsx
// НЕПРАВИЛЬНО — при первом коммите stageProgress = null
<div>{session.stageProgress.percentComplete}%</div>
```

**Что произойдёт:** `TypeError: Cannot read 'percentComplete' of null`.

**Правильно:** `{live && live.totalRows > 0 && (...)}`.

### Ошибка 5: бесконечный лог `[ImportsHub] ← StageProgress`

```ts
// НЕПРАВИЛЬНО — Info-уровень, спамит консоль
console.info('[ImportsHub] ← StageProgress', e);
```

**Правильно:** `console.debug(...)` — DevTools по умолчанию скрывает debug.
Для важных событий (`SessionStatus`, `StageStarted/Completed`) — `console.info`.

---

## 🔍 Диагностика проблем с SignalR

### Сценарий 1: «прогресс не приходит»

1. **DevTools → Network → WS** — есть ли соединение `/hubs/imports`?
   - Нет → проблема в Vite proxy (нет `ws: true`?) или backend не принимает.
   - Yes → шаг 2.
2. **DevTools → Console** — видно `[ImportsHub] ✓ connected`?
   - Нет → ошибка `start()`, смотри причину.
   - Yes → шаг 3.
3. **Видно `[ImportsHub] → JoinSession <id>`?**
   - Нет → не вызвали `joinSession` после `start`.
   - Yes → шаг 4.
4. **Backend-логи** — приходит `StageProgress` в pipeline?
   - Нет → Pipeline не дошёл до Validate (упал на Parse?).
   - Yes → событие отправлено, но не получено клиентом — проверь группу
     `session:{id}` совпадает.

### Сценарий 2: «события приходят, но UI не обновляется»

- В консоли `[ImportsHub] ← StageProgress` — да, в payload корректный sessionId?
- В useImportSession `if (e.sessionId !== sessionIdRef.current)` — не отрезает ли?
- `setSession` вызывается? — проверь React DevTools → Components → useImportSession.

### Сценарий 3: «после reconnect события дублируются»

Это может означать, что `joinSession` вызывается несколько раз. Наша обёртка
вызывает `JoinSession` только при `state === Connected`. Если ты сделал
свою подписку через `connection.onreconnected(...)` — убедись, что это
происходит один раз.

---

## 📍 Применение в проекте

| Слой | Файл |
|------|------|
| Backend hub | `KiloImportService.Api/Hubs/ImportProgressHub.cs` |
| Backend публикация | `KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs` |
| UI обёртка | `KiloImportService.Web/src/services/importsHub.ts` |
| UI применение | `KiloImportService.Web/src/hooks/useImportSession.ts` |
| UI отображение | `KiloImportService.Web/src/components/ImportSession/SessionProgress.tsx` |
| Vite WebSocket proxy | `KiloImportService.Web/vite.config.ts` (`/hubs` + `ws: true`) |

---

## 🎯 Чек-лист при добавлении нового SignalR-события

- [ ] Backend: `await _hub.Clients.Group(g).SendAsync("MyEvent", payload, ct);`
- [ ] При публикации в цикле — добавить **троттлинг** (`% notifyEvery == 0`)
- [ ] UI: тип события в `services/importsHub.ts` (`MyEventEvent` interface)
- [ ] UI: handler-проп в `ImportsHubHandlers`
- [ ] UI: `connection.on('MyEvent', handler)` внутри `createImportsHub`
- [ ] UI: подписка в `useImportSession` с проверкой `sessionId`
- [ ] UI: `console.debug` если событие частое, `console.info` если редкое
- [ ] При unmount компонента — `hub.stop()` (cleanup в `useEffect`)
