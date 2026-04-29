using KiloImportService.Api.Domain.Importing;

namespace KiloImportService.Api.Data.Entities;

/// <summary>
/// Этап сессии импорта (Upload → Parse → Validate → Apply).
/// Используется для real-time прогресса через SignalR.
/// </summary>
public class ImportSessionStage
{
    public long Id { get; set; }
    public Guid ImportSessionId { get; set; }
    public ImportSession Session { get; set; } = null!;

    public ImportStageKind Kind { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Прогресс 0–100 (для SignalR).</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Краткое сообщение этапа («2451/3000 строк»).</summary>
    public string? Message { get; set; }

    public bool IsSuccess { get; set; }
}
