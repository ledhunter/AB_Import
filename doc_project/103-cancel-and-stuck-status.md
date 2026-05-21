# 🛑 Cancel-button не отменяет «зависший» импорт — finalize промежуточных статусов

## 📋 Описание

После прогона Репино-Парк пользователь нажимал «Отменить» во время Validate, но UI продолжал показывать «Импорт в процессе. Этап: Валидация строк 40%». В логах видно ровно один симптом:

```
Cancel(96214457-...): нет активного CTS — задача уже завершилась или не стартовала
HTTP POST /api/imports/.../cancel responded 200 in 8 ms
```

Cancel-endpoint отвечает 200, но статус сессии в БД не меняется → UI висит навсегда.

---

## Корень проблемы

Два независимых пути приводят к «зависанию» сессии в промежуточном статусе:

### Путь 1 — Upload-background проглатывает exception

`ImportsController.Upload` запускает фоновую задачу через `_backgroundFactory.StartNew(async () => ...)`. Старый код:

```csharp
catch (Exception ex)
{
    log.LogError(ex, "ParseAndValidate failed for session {SessionId}", sessionId);
}
finally
{
    _cancellation.Unregister(sessionId);
}
```

Если `pipeline.ParseAndValidateAsync` бросает unhandled (Visary 500, БД-ошибка, JSON-deserialization-fail, OOM, etc.) — exception **логируется и забывается**. Статус сессии остаётся `Parsing` или `Validating`, CTS снимается через `Unregister`. UI продолжает рисовать «в процессе» по последнему SignalR-snapshot'у.

### Путь 2 — Cancel-endpoint не финализирует промежуточные статусы

Старый код Cancel:

```csharp
var cancelled = _cancellation.Cancel(id);
if (cancelled) { /* статус обновит сам pipeline в catch (OCE) */ return Accepted(...); }

// CTS нет — задача уже завершилась.
if (session.Status is ImportStatus.Pending or ImportStatus.Validated)
{
    session.Status = ImportStatus.Cancelled;
    // ...
}
return Ok(new { ..., status = session.Status.ToString() });
```

Если `session.Status` — `Parsing` / `Validating` / `Applying` (зависший после пути 1, или race между `Unregister` и завершением финального `TransitionAsync`), условие `is Pending or Validated` **не срабатывает**. Endpoint просто возвращает текущий статус. UI на 200 OK ничего не делает.

---

## ✅ Правильная реализация

### Pipeline: общий helper `ForceFinalizeAsync`

```csharp
// ImportPipeline.cs
public async Task ForceFinalizeAsync(
    Guid sessionId, ImportStatus finalStatus, string reason, CancellationToken ct = default)
{
    if (finalStatus is not (ImportStatus.Failed or ImportStatus.Cancelled or ImportStatus.Applied))
        throw new ArgumentException(...);

    var session = await _serviceDb.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
    if (session is null) return;
    if (session.Status is ImportStatus.Applied or ImportStatus.Failed or ImportStatus.Cancelled)
        return; // защита от повторной финализации
    session.Status = finalStatus;
    session.CompletedAt = DateTimeOffset.UtcNow;
    session.ErrorMessage = reason;
    await _serviceDb.SaveChangesAsync(ct);
    await _hub.Clients.Group(ImportProgressHub.GroupName(sessionId))
        .SendAsync("SessionStatus", new { sessionId, status = finalStatus.ToString() }, ct);
}
```

### Upload-background: финализирует Failed при unhandled

```csharp
catch (Exception ex)
{
    log.LogError(ex, "ParseAndValidate failed for session {SessionId}", sessionId);
    try
    {
        await pipeline.ForceFinalizeAsync(
            sessionId, ImportStatus.Failed,
            $"Фоновая обработка упала: {ex.Message}",
            default);
    }
    catch (Exception finalizeEx) { log.LogError(finalizeEx, ...); }
}
```

### Cancel-endpoint: финализирует любой не-финальный статус

```csharp
if (session.Status is not (ImportStatus.Applied or ImportStatus.Failed or ImportStatus.Cancelled))
{
    await _pipeline.ForceFinalizeAsync(
        id, ImportStatus.Cancelled,
        "Импорт отменён пользователем (фоновая задача уже не активна).",
        ct);
    return Ok(new { sessionId = id, status = ImportStatus.Cancelled.ToString() });
}
```

### ⚠️ Важно

- `ForceFinalizeAsync` **обязан** слать SignalR-событие `SessionStatus` — иначе UI не выйдет из «в процессе». Polling `GET /api/imports/{id}` в UI тоже работает, но реактивный канал быстрее.
- `default` для CT в Upload-катче: токен сессии уже Cancel-нут (или скоро будет), а нам нужно гарантированно записать финальный статус в БД.
- Гард `if (session.Status is Applied/Failed/Cancelled) return;` защищает от повторной финализации: возможна гонка между `Cancel`-endpoint, `Upload`-фоновым катчем, и финальным `TransitionAsync` из самого pipeline.

---

## ❌ Типичные ошибки

### 1. «Логирую и возвращаюсь» в фоновой задаче

```csharp
catch (Exception ex) { log.LogError(ex, ...); } // 👎 статус остался в БД промежуточным
```

Фоновая задача без обновления статуса делает сессию «вечной» — её не отменить, не перезапустить, не отмаппить в UI.

### 2. Cancel переводит в Cancelled только «безопасные» статусы

```csharp
if (session.Status is ImportStatus.Pending or ImportStatus.Validated) { ... }
// Parsing/Validating/Applying игнорируются — но именно в них застревают зависшие сессии
```

Контр-аргумент «не хочу перетереть статус активной фоновой» снимается тем, что `CTS == null` ⇒ задача уже не активна. А если активна — отработает первый `if` (`cancelled == true`), и pipeline сам обновит статус через `MarkCancelledAsync`.

### 3. `TransitionAsync(session, ImportStatus.Failed, ct)` с `ct`, который Cancel-нут

`TransitionAsync` использует тот же `ct`, что и pipeline; если pipeline бросил OCE из-за Cancel, попытка `SaveChangesAsync(ct)` тоже бросит OCE. Поэтому в `ForceFinalizeAsync` используем `default`-токен (или передаём `CancellationToken.None`).

---

## 📍 Применение в проекте

| Слой | Файл | Что изменилось |
|------|------|----------------|
| Pipeline | [ImportPipeline.cs](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs) | Новый public `ForceFinalizeAsync(sessionId, finalStatus, reason, ct)` |
| Controller | [ImportsController.cs:99](../KiloImportService.Api/Controllers/ImportsController.cs#L99) | Upload-background: при unhandled → `ForceFinalizeAsync(..., Failed, ...)` |
| Controller | [ImportsController.cs:504](../KiloImportService.Api/Controllers/ImportsController.cs#L504) | Cancel-endpoint: при любом не-финальном статусе → `ForceFinalizeAsync(..., Cancelled, ...)` |

---

## 🎯 Чек-лист при правках pipeline

- [ ] Любой новый код, способный бросить unhandled из `ParseAndValidateAsync` / `ApplyAsync`, должен быть либо обёрнут в try/catch с явной финализацией, либо завязан на `ForceFinalizeAsync` через Upload-background.
- [ ] Не использовать связанный с pipeline `ct` для финализации статуса — токен может быть Cancel-нут, и `SaveChangesAsync` упадёт.
- [ ] Любой новый промежуточный статус (если такой появится) — добавить в гард `is Applied/Failed/Cancelled` в Cancel-endpoint, чтобы автоматически финализировался при «нет CTS».

---

## Связанные документы

- [16-import-cancellation.md](16-import-cancellation.md) — оригинальная сборка Cancel-flow (CTS-реестр, OCE-проброс в парсерах)
- [15-signalr-progress.md](15-signalr-progress.md) — троттлинг StageProgress, `SessionStatus`-событие
- [101-rooms-multi-site-by-project.md](101-rooms-multi-site-by-project.md) — per-row site resolve, который в Validate может породить долгий pre-pass
