# 📊 Логирование сквозное: от boot-маркера в браузере до Apply-стадии

## 📋 Описание

«Белый экран» на целевом стенде = в логах backend была пустота, в DevTools-консоли — тоже
(prod-bundle вырезал все `devLog/devInfo` через `import.meta.env.DEV`).
Эта правка добавляет **видимый поток событий** в обе стороны:

| Точка | Где видно | Что пишется |
|-------|-----------|--------------|
| `index.html` загружен | DevTools → Console | `[ab-fm-import] index.html loaded` |
| `main.tsx` стартовал | DevTools → Console | `[ab-fm-import] SPA boot start` + build/base/apiPrefix/href/UA |
| React смонтировался | DevTools → Console | `[ab-fm-import] React mounted ✓` |
| Глобальный `window.onerror` | DevTools → Console | `[ab-fm-import] window.onerror` + message/source/line/col |
| `unhandledrejection` | DevTools → Console | `[ab-fm-import] unhandledrejection` + reason |
| Backend старт | `docker logs backend` | дамп URLs/PathBase/CORS/Visary/JWT/Swagger/DB + список всех маршрутов |
| HTTP-запрос | `docker logs backend` | `HTTP {Method} {Path} → {Status} за {ms} мс (scheme=… host=… pathBase=…)` |
| Pipeline стадии | `docker logs backend` | `▶ PARSE`, `◀ PARSE done за N мс`, `▶ VALIDATE`, `▶ APPLY`, `◀ APPLY done` |
| 500 ошибка | `docker logs backend` | `Необработанное исключение в pipeline. {Method} {Path} → 500` + stack |
| Диагностика | `GET /diag` | JSON со всем стартовым состоянием + список endpoint'ов |

---

## ✅ Правильная реализация

### Frontend — три уровня логи (`src/services/devLog.ts`)

```ts
// dev* — вырезаются в prod через import.meta.env.DEV (как было)
export const devLog  = (...a: unknown[]) => { if (DEV) console.log(...a); };
export const devInfo = (...a: unknown[]) => { if (DEV) console.info(...a); };
// ...

// boot*/critical* — ВСЕГДА видны (даже в prod-bundle). С префиксом [ab-fm-import].
export const bootLog       = (...a: unknown[]) => console.info('[ab-fm-import]', ...a);
export const bootWarn      = (...a: unknown[]) => console.warn('[ab-fm-import]', ...a);
export const criticalError = (...a: unknown[]) => console.error('[ab-fm-import]', ...a);
```

### Frontend — глобальные error handlers (`src/main.tsx`)

```ts
window.addEventListener('error', (e) => {
  criticalError('window.onerror', { message: e.message, source: e.filename, line: e.lineno });
});
window.addEventListener('unhandledrejection', (e) => {
  criticalError('unhandledrejection', e.reason);
});
```

### Frontend — fallback при сломе React (`src/main.tsx`)

```tsx
try {
  createRoot(rootEl).render(<StrictMode><App /></StrictMode>);
  bootLog('React mounted ✓');
} catch (err) {
  criticalError('createRoot/render упал:', err);
  rootEl.innerHTML = '<div>Не удалось запустить интерфейс. Смотри консоль.</div>';
}
```

### Backend — расширенный request-log (`Program.cs`)

```csharp
app.UseSerilogRequestLogging(opt =>
{
    opt.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} → {StatusCode} за {Elapsed:0} мс " +
        "(scheme={Scheme} host={Host} pathBase={PathBase})";
    opt.EnrichDiagnosticContext = (diag, http) =>
    {
        diag.Set("Scheme", http.Request.Scheme);
        diag.Set("Host",   http.Request.Host.Value ?? "-");
        diag.Set("PathBase", http.Request.PathBase.HasValue ? http.Request.PathBase.Value : "-");
        diag.Set("UserAgent", (string?)http.Request.Headers.UserAgent ?? "-");
    };
});
```

### Backend — глобальный exception-handler (`Program.cs`)

```csharp
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        Log.Error(ex, "Необработанное исключение в pipeline. {Method} {Path}{Query} → 500",
            ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString);
        if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 500;
    }
});
```

### Backend — стартовый дамп конфигурации (`Program.cs`)

```text
[12:34:56.789 INF] ┌── Конфигурация сервиса ─────────────────────────────────────
[12:34:56.789 INF] │ URLs        : http://+:5000
[12:34:56.789 INF] │ PathBase    : /api/ab-fm-import
[12:34:56.789 INF] │ CORS        : https://abdev.moscow.alfaintra.net
[12:34:56.789 INF] │ Visary API  : https://isup-alfa-test.k8s.npc.ba
[12:34:56.789 INF] │ JWT auth    : ENABLED
[12:34:56.789 INF] │ Swagger     : DISABLED
[12:34:56.789 INF] │ DB conn-str : Host=postgres;Port=5432;Database=ab_fm_import;Username=ab_fm_import_user;Password=***
[12:34:56.789 INF] └─────────────────────────────────────────────────────────────
[12:34:56.790 INF] Зарегистрировано 42 HTTP-маршрутов:
[12:34:56.790 INF]     *      /api/imports
[12:34:56.790 INF]     *      /api/imports/{id}
[12:34:56.790 INF]     *      /diag
[12:34:56.790 INF]     *      /health
...
```

### Backend — Pipeline стадии (`Domain/Pipeline/ImportPipeline.cs`)

```text
[12:35:01.234 INF] Upload→Pipeline: importType=rooms file='Rooms.xlsx' project=42 site=78 user=ivanov secondary=(нет)
[12:35:01.500 INF] Import session a3f… created: type=rooms file=Rooms.xlsx (15234 bytes)
[12:35:01.520 INF] Session a3f…: ▶ PARSE stage  (mapper=RoomsFormImportMapper, file='Rooms.xlsx', format=Xlsx)
[12:35:02.140 INF] Session a3f…: ◀ PARSE done за 620 мс — rows=87 file-errors=0 sheets=[Квартиры, Кладовые]
[12:35:02.150 INF] Session a3f…: starting VALIDATE stage
[12:35:02.155 INF] Session a3f…: calling mapper.ValidateAsync
[12:35:02.890 INF] Session a3f…: mapper.ValidateAsync returned rows=87 errors=0
[12:35:03.020 INF] Session a3f…: ◀ VALIDATE done — finalStatus=Validated valid=85 invalid=2 fileErrors=0
```

### ⚠️ Важно

- **Никаких секретов в логе**. `MaskConnectionString()` маскирует `Password=…`. JWT-токены не печатаем.
- `bootLog` — это `console.info`, не `console.log` — в DevTools-фильтре можно отделить от App-логов.
- На целевом стенде Serilog уже выведен в Console — `kubectl logs <pod>` показывает всё без дополнительной конфигурации (если в helm-чарте нет sidecar log-collector'а, который перехватывает stdout).

---

## ❌ Типичные ошибки

```ts
// НЕПРАВИЛЬНО — devLog в boot-пути.
import { devLog } from './services/devLog';
devLog('SPA started');  // 👈 в prod-bundle вырезается — белый экран без диагностики
```

```csharp
// НЕПРАВИЛЬНО — Log.Information без структурных полей.
Log.Information($"Session {sessionId} started PARSE");
// 👈 теряется structured-search в Serilog (Seq/ELK не сгруппирует по SessionId)
```

```csharp
// НЕПРАВИЛЬНО — логируем connection-string как есть.
Log.Information("DB: {Cs}", builder.Configuration.GetConnectionString("AbFmImport"));
// 👈 Password=... уходит в kubectl logs / Loki / архив на год
```

---

## 📍 Применение в проекте

| Слой | Файл | Что добавлено |
|------|------|----------------|
| Backend bootstrap | `KiloImportService.Api/Program.cs:25-48` | Enrich + ранний баннер с PID/Machine/Framework/Env |
| Backend конфиг-дамп | `KiloImportService.Api/Program.cs` (хвост) | Блок «┌── Конфигурация сервиса ──» + список endpoint'ов |
| Backend request-log | `KiloImportService.Api/Program.cs` | `UseSerilogRequestLogging` с PathBase/Scheme/UA |
| Backend exception | `KiloImportService.Api/Program.cs` | глобальный try-catch middleware |
| Backend diag | `KiloImportService.Api/Program.cs` | `GET /diag` — JSON со стартовым состоянием |
| Pipeline | `KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs` | `▶ PARSE / ◀ PARSE done за X мс / ▶ VALIDATE / ▶ APPLY / ◀ APPLY done` |
| Controllers | `KiloImportService.Api/Controllers/ImportsController.cs` | `POST /api/imports — Upload: …` на каждом endpoint'е |
| Frontend log | `KiloImportService.Web/src/services/devLog.ts` | `bootLog/bootWarn/criticalError` (всегда-on) |
| Frontend boot | `KiloImportService.Web/src/main.tsx` | window.onerror + unhandledrejection + try/catch render |
| HTML маркер | `KiloImportService.Web/index.html` | `<script>console.info('[ab-fm-import] index.html loaded …')</script>` |

---

## 🎯 Чек-лист диагностики «белого экрана»

1. **DevTools → Console**:
   - Видишь `[ab-fm-import] index.html loaded`? → bundle хотя бы дошёл до браузера.
   - Видишь `[ab-fm-import] SPA boot start`? → `main.tsx` исполнился. Проверь `base=` и `apiPrefix=` в логе — совпадают ли с reverse-proxy.
   - НЕ видишь? → проблема между `<script type="module" src="/src/main.tsx">` и его загрузкой. В Network проверь 404 на JS-бандл.
   - Есть `[ab-fm-import] window.onerror`? → строка/файл из лога указывает место падения.
2. **`docker logs backend` / `kubectl logs <pod>`**:
   - Видишь баннер `┌── Конфигурация сервиса ──`? → backend стартовал.
   - В блоке «Зарегистрировано N маршрутов» есть `/api/imports`? → контроллер подхватился.
   - Видишь `HTTP GET /diag → 200 (scheme=https host=… pathBase=/api/ab-fm-import)`? → reverse-proxy дотянулся до backend с правильным PathBase.
   - Видишь `HTTP POST /api/imports → 500`? → дальше идёт stack-trace.
3. **`GET https://<host>/api/ab-fm-import/diag`**:
   - `pathBase` == ожидаемый prefix?
   - `cors` содержит origin UI?
   - `endpoints[]` содержит `/api/imports`?

См. также:
- doc 147 — base-path-reverse-proxy (парный документ, тот же класс проблем)
- doc 121 — security baseline (unsafe-formatstring закрыт через `devLog` wrapper)
