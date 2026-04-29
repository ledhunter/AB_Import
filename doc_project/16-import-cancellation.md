# 🛑 Отмена сессии импорта

## 📋 Описание

Кнопка «Отменить» в UI должна **реально** прервать фоновую задачу, а не просто
поставить статус `Cancelled` в БД. Иначе фон продолжит писать в Visary, и
половина данных «утечёт» при отмене на середине Apply.

Реализовано через **Singleton-реестр CancellationTokenSource** + явная обработка
`OperationCanceledException` в Pipeline и парсерах.

---

## 🏗️ Архитектура

```
┌──────────────────┐  Cancel  ┌────────────────────────────┐
│ ImportsController├─────────►│ IImportSessionCancellation │
│  (POST /cancel)  │          │  (Singleton, in-memory)    │
└──────────────────┘          │                            │
                              │  Dictionary<Guid, CTS>     │
                              └─────────┬──────────────────┘
                                        │ CTS.Cancel()
                                        ▼
┌──────────────────┐                    ┌────────────────┐
│ ImportsController│ Register/Unregister│ Background     │
│  (POST /imports) │◄──────────────────►│ Task           │
└──────────────────┘                    │  (Pipeline)    │
                                        │                │
                                        │ ct.ThrowIfCancellationRequested()
                                        │  → MarkCancelledAsync
                                        └────────────────┘
```

---

## ✅ Правильная реализация

### Шаг 1: Singleton-реестр CTS

```csharp
// KiloImportService.Api/Domain/Pipeline/ImportSessionCancellation.cs
public interface IImportSessionCancellation
{
    CancellationToken Register(Guid sessionId);
    void Unregister(Guid sessionId);
    bool Cancel(Guid sessionId);
}

public sealed class ImportSessionCancellation : IImportSessionCancellation, IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _registry = new();

    public CancellationToken Register(Guid sessionId)
    {
        var cts = new CancellationTokenSource();
        if (_registry.TryRemove(sessionId, out var old))
        {
            // Сценарий: повторная регистрация (повтор Apply, например).
            try { old.Dispose(); } catch { }
        }
        _registry[sessionId] = cts;
        return cts.Token;
    }

    public void Unregister(Guid sessionId)
    {
        if (_registry.TryRemove(sessionId, out var cts))
            try { cts.Dispose(); } catch { }
    }

    public bool Cancel(Guid sessionId)
    {
        if (!_registry.TryGetValue(sessionId, out var cts)) return false;
        try { cts.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }
}
```

```csharp
// Program.cs
builder.Services.AddSingleton<IImportSessionCancellation, ImportSessionCancellation>();
```

### Шаг 2: Upload регистрирует CTS, фоновая задача его получает

```csharp
// ImportsController.Upload
var ctSession = _cancellation.Register(session.Id);
var sessionId = session.Id;
_ = _backgroundFactory.StartNew(async () =>
{
    using var scope = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
    var pipeline = scope.ServiceProvider.GetRequiredService<ImportPipeline>();
    try
    {
        await pipeline.ParseAndValidateAsync(sessionId, ctSession);
    }
    catch (OperationCanceledException) when (ctSession.IsCancellationRequested)
    {
        // Pipeline уже выставил Cancelled через MarkCancelledAsync.
        scope.ServiceProvider.GetRequiredService<ILogger<ImportsController>>()
            .LogInformation("ParseAndValidate cancelled for session {SessionId}", sessionId);
    }
    catch (Exception ex) { /* Failed-логика */ }
    finally
    {
        _cancellation.Unregister(sessionId);   // 👈 ВСЕГДА чистим реестр
    }
});
```

### Шаг 3: Apply использует LinkedTokenSource

```csharp
// ImportsController.Apply — синхронный, но тоже отменяемый
var ctApply = _cancellation.Register(id);
using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ctApply);
try
{
    await _pipeline.ApplyAsync(id, linked.Token);
}
catch (OperationCanceledException) when (ctApply.IsCancellationRequested)
{
    // CancelRequested — пайплайн пометит Cancelled.
}
finally { _cancellation.Unregister(id); }
```

### Шаг 4: Cancel-endpoint различает три сценария

```csharp
[HttpPost("{id:guid}/cancel")]
public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
{
    var session = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (session is null) return NotFound();

    if (session.Status == ImportStatus.Applied)
        return Conflict(new { error = "Сессия уже применена, отмена невозможна." });

    if (session.Status == ImportStatus.Cancelled)
        return Ok(new { sessionId = id, status = session.Status.ToString() });

    // 1) Активная фоновая задача — посылаем сигнал, пайплайн сам выставит Cancelled.
    if (_cancellation.Cancel(id))
        return Accepted(new { sessionId = id, status = "CancelRequested" });

    // 2) Задачи нет (Pending до старта / Validated в ожидании Apply) — помечаем БД.
    if (session.Status is ImportStatus.Pending or ImportStatus.Validated)
    {
        session.Status = ImportStatus.Cancelled;
        session.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
    return Ok(new { sessionId = id, status = session.Status.ToString() });
}
```

### Шаг 5: Pipeline ловит OperationCanceledException и помечает Cancelled

```csharp
// ImportPipeline.cs
public async Task ParseAndValidateAsync(Guid sessionId, CancellationToken ct)
{
    try
    {
        await ParseAndValidateCoreAsync(sessionId, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // 👇 Помечаем Cancelled независимым ct — иначе SaveChangesAsync(ct)
        //    тоже отменится и статус не запишется.
        await MarkCancelledAsync(sessionId, "Импорт отменён пользователем.", default);
        throw;
    }
}

private async Task MarkCancelledAsync(Guid sessionId, string reason, CancellationToken ct)
{
    try
    {
        var session = await _serviceDb.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;
        // Если кто-то уже выставил финальный статус — не перетираем.
        if (session.Status is ImportStatus.Applied or ImportStatus.Failed or ImportStatus.Cancelled)
            return;
        session.Status = ImportStatus.Cancelled;
        session.CompletedAt = DateTimeOffset.UtcNow;
        session.ErrorMessage = reason;
        await _serviceDb.SaveChangesAsync(ct);
        await _hub.Clients.Group(ImportProgressHub.GroupName(sessionId))
            .SendAsync("SessionStatus", new { sessionId, status = "Cancelled" }, ct);
    }
    catch (Exception ex) { _log.LogError(ex, "MarkCancelledAsync failed"); }
}
```

### Шаг 6: парсеры пробрасывают OperationCanceledException

Это **критичный** момент — без него Cancel не работает на этапе Parse.

```csharp
// CsvParser.cs / XlsxParser.cs / ExcelDataReaderParser.cs
try
{
    // ... тело парсинга с ct.ThrowIfCancellationRequested() ...
}
catch (OperationCanceledException)
{
    // 👇 Отмену пробрасываем НАРУЖУ — это управляющая ситуация,
    //    а не ошибка парсинга.
    throw;
}
catch (Exception ex)
{
    errors.Add(new ParseError(null, $"Не удалось прочитать ...: {ex.Message}"));
}
```

### ⚠️ Важно

- **`when (ct.IsCancellationRequested)`** в catch — фильтруем только наш Cancel,
  не «случайный» OperationCanceledException из библиотек.
- **`MarkCancelledAsync` использует `default` (CT.None)** — если бы передали
  отменённый ct, `SaveChangesAsync(ct)` тоже бы упал, и статус не записался.
- **Idempotency в `MarkCancelledAsync`** — проверка `Status is Applied/Failed/Cancelled`
  защищает от перезаписи финального статуса (race с MarkFailed).
- **`finally _cancellation.Unregister(id)`** — обязательно, иначе CTS будет
  висеть в реестре после Failed/Applied и Cancel другой сессии будет работать.

---

## ❌ Типичные ошибки

### Ошибка 1: фоновая задача с `CancellationToken.None`

```csharp
// НЕПРАВИЛЬНО — задача не отменяема
_ = _backgroundFactory.StartNew(async () =>
{
    await pipeline.ParseAndValidateAsync(sessionId, CancellationToken.None);  // ❌
});
```

**Что произойдёт:** Cancel-endpoint поставит `Cancelled` в БД, но pipeline
продолжит писать строки. Race condition.

**Правильно:** `_cancellation.Register(sessionId)` → передать токен в pipeline.

### Ошибка 2: ловля `Exception` без исключения OperationCanceledException

```csharp
// НЕПРАВИЛЬНО — парсер «съедает» отмену
try { /* parse */ }
catch (Exception ex)  // ❌ ловит и OperationCanceledException
{
    errors.Add(new ParseError(null, ex.Message));   // отмена → "ошибка парсинга"
}
```

**Что произойдёт:** Cancel на этапе Parse не сработает: парсер вернёт `ParseResult`
с `errors`, pipeline пойдёт дальше в Validate, и UI увидит «упал из-за ошибки
парсинга», а не «отменён».

**Этот баг был найден тестом `CsvParserTests.RespectsCancellationToken`** — см.
[17-backend-tests-xunit.md](./17-backend-tests-xunit.md).

**Правильно:** явный `catch (OperationCanceledException) { throw; }` ПЕРЕД общим catch.

### Ошибка 3: SaveChangesAsync(ct) при пометке Cancelled

```csharp
// НЕПРАВИЛЬНО
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    session.Status = ImportStatus.Cancelled;
    await _serviceDb.SaveChangesAsync(ct);   // ❌ ct уже отменён → exception
}
```

**Что произойдёт:** статус Cancelled не запишется в БД (SaveChanges упадёт на
проверке ct), UI будет видеть `Validating` навсегда.

**Правильно:** передавать `default` в `MarkCancelledAsync`, чтобы запись прошла
независимо от исходного ct.

### Ошибка 4: Unregister пропущен при failed-выходе

```csharp
// НЕПРАВИЛЬНО
try
{
    await pipeline.ParseAndValidateAsync(sessionId, ct);
    _cancellation.Unregister(sessionId);   // ❌ не выполнится при exception
}
catch { /* ... */ }
```

**Что произойдёт:** при Failed CTS остаётся в реестре. Если sessionId переиспользуется
или просто случайно совпадает GUID (маловероятно, но возможно) — Cancel
другой сессии затронет старую.

**Правильно:** `finally { _cancellation.Unregister(sessionId); }`.

### Ошибка 5: cancel-endpoint меняет статус Validated/Pending когда задача активна

```csharp
// НЕПРАВИЛЬНО — race condition
session.Status = ImportStatus.Cancelled;     // ❌ перетёр Applying → Cancelled
await _db.SaveChangesAsync(ct);
_cancellation.Cancel(id);  // потом всё равно отправили сигнал
```

**Что произойдёт:** Cancel прилетел во время Apply. Контроллер сначала перезаписал
статус в БД, потом сигнал отмены прилетел в pipeline, который попробует выставить
Cancelled (idempotent, ОК). Но между двумя шагами `Apply` уже мог записать половину
строк в Visary.

**Правильно:** сначала `_cancellation.Cancel(id)` (это атомарно), и только если
`Cancel` вернул `false` (нет активной задачи) — менять статус в БД руками. Так
сделано в нашем endpoint'е.

---

## 🧪 Тестирование

| Сценарий | Тест |
|----------|------|
| Register возвращает не-cancelled токен | `ImportSessionCancellationTests.Register_ReturnsTokenThatIsNotCancelled` |
| Cancel зарегистрированной → token.IsCancellationRequested = true | `Cancel_TokenForRegisteredSession_BecomesCancelled` |
| Cancel неизвестной сессии → false | `Cancel_UnknownSession_ReturnsFalse` |
| Unregister → следующий Cancel возвращает false | `Unregister_RemovesCts_AndCancelAfterReturnsFalse` |
| Двойной Register одной сессии → новый токен работает | `DoubleRegister_ForSameSession_OldTokenStaysAlive_NewTokenWorks` |
| Парсеры реагируют на ct | `CsvParserTests.RespectsCancellationToken` |
| Маппер реагирует на ct | `RoomsImportMapperTests.Validate_RespectsCancellationToken` |

Запуск:
```powershell
dotnet test KiloImportService.Api.Tests/KiloImportService.Api.Tests.csproj
```

---

## 📍 Применение в проекте

| Слой | Файл |
|------|------|
| Реестр CTS | `KiloImportService.Api/Domain/Pipeline/ImportSessionCancellation.cs` |
| Регистрация в DI | `KiloImportService.Api/Program.cs` |
| Фоновая задача (ParseValidate) | `KiloImportService.Api/Controllers/ImportsController.cs` (Upload) |
| Apply с linked CT | `KiloImportService.Api/Controllers/ImportsController.cs` (Apply) |
| Cancel-endpoint | `KiloImportService.Api/Controllers/ImportsController.cs` (Cancel) |
| Pipeline catch | `KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs` (ParseAndValidate, Apply) |
| MarkCancelledAsync | `KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs` |
| Парсеры (catch OCE) | `KiloImportService.Api/Domain/Importing/Parsers/*.cs` |
| UI Cancel | `KiloImportService.Web/src/services/importsService.ts` (`cancelImport`) |
| UI hook | `KiloImportService.Web/src/hooks/useImportSession.ts` (`cancel`) |

---

## 🎯 Чек-лист при работе с длительными операциями

- [ ] Метод принимает `CancellationToken ct` как параметр
- [ ] В цикле есть `ct.ThrowIfCancellationRequested()` (хотя бы раз в N итераций)
- [ ] `try/catch (OperationCanceledException) { throw; }` ДО общего `catch (Exception)`
- [ ] Если делаешь cleanup в catch (запись в БД) — используй `CancellationToken.None`,
      иначе SaveChanges тоже отменится
- [ ] `finally { _cancellation.Unregister(sessionId); }` обязательно
- [ ] Тест с уже отменённым CTS — проверяет, что метод бросает
      `OperationCanceledException`, а не возвращает «ошибку»
