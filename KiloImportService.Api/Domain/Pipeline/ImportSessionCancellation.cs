using System.Collections.Concurrent;

namespace KiloImportService.Api.Domain.Pipeline;

/// <summary>
/// Реестр <see cref="CancellationTokenSource"/> для активных фоновых задач импорта.
/// Регистрация — на <see cref="Register"/> (когда стартует фоновая задача),
/// снятие — на <see cref="Unregister"/> (по завершению любым исходом).
///
/// Cancel-endpoint вытаскивает CTS по sessionId и вызывает <see cref="Cancel"/>:
/// фоновая задача получит <see cref="OperationCanceledException"/> на ближайшем
/// <c>ct.ThrowIfCancellationRequested()</c>, а <see cref="ImportPipeline"/> поставит
/// статусу сессии <c>Cancelled</c>.
///
/// Регистрируется как Singleton в DI.
/// </summary>
public interface IImportSessionCancellation
{
    /// <summary>Создаёт CTS для сессии и сохраняет его в реестре. Возвращает токен.</summary>
    CancellationToken Register(Guid sessionId);

    /// <summary>Снять регистрацию (после завершения / падения фоновой задачи).</summary>
    void Unregister(Guid sessionId);

    /// <summary>
    /// Запросить отмену активной фоновой задачи. Возвращает <c>true</c>, если CTS был
    /// найден и Cancel прошёл; <c>false</c>, если задача не активна (уже завершилась
    /// или сессии нет в реестре — например, ещё не дошла до фонового запуска).
    /// </summary>
    bool Cancel(Guid sessionId);
}

public sealed class ImportSessionCancellation : IImportSessionCancellation, IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _registry = new();
    private readonly ILogger<ImportSessionCancellation> _log;

    public ImportSessionCancellation(ILogger<ImportSessionCancellation> log)
    {
        _log = log;
    }

    public CancellationToken Register(Guid sessionId)
    {
        var cts = new CancellationTokenSource();
        // Сценарий: повторная регистрация одного и того же sessionId.
        // Не должно случаться, но если случилось — освободим старый CTS.
        if (_registry.TryRemove(sessionId, out var old))
        {
            _log.LogWarning("Session {SessionId} re-registered — старый CTS будет dispose'нут", sessionId);
            try { old.Dispose(); } catch { /* ignore */ }
        }
        _registry[sessionId] = cts;
        return cts.Token;
    }

    public void Unregister(Guid sessionId)
    {
        if (_registry.TryRemove(sessionId, out var cts))
        {
            try { cts.Dispose(); } catch { /* ignore */ }
        }
    }

    public bool Cancel(Guid sessionId)
    {
        if (!_registry.TryGetValue(sessionId, out var cts))
        {
            _log.LogInformation("Cancel({SessionId}): нет активного CTS — задача уже завершилась или не стартовала", sessionId);
            return false;
        }
        try
        {
            cts.Cancel();
            _log.LogInformation("Cancel({SessionId}): CTS.Cancel() выполнен", sessionId);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _registry)
        {
            try { kvp.Value.Dispose(); } catch { /* ignore */ }
        }
        _registry.Clear();
    }
}
