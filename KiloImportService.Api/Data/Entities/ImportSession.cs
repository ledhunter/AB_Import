using KiloImportService.Api.Domain.Importing;

namespace KiloImportService.Api.Data.Entities;

/// <summary>
/// Корневая сущность одной сессии импорта (одна загрузка файла = одна сессия).
/// Хранится в <c>import_service_db.import.import_sessions</c>.
/// </summary>
public class ImportSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Идентификатор типа импорта из реестра (rooms, shareAgreements, …).</summary>
    public string ImportTypeCode { get; set; } = null!;

    public string FileName { get; set; } = null!;
    public long FileSize { get; set; }
    public FileFormat FileFormat { get; set; }

    public ImportStatus Status { get; set; } = ImportStatus.Pending;

    /// <summary>Целевой проект Visary (необязательный для некоторых типов импорта).</summary>
    public int? VisaryProjectId { get; set; }

    /// <summary>Целевой объект строительства Visary (необязательный).</summary>
    public int? VisarySiteId { get; set; }

    /// <summary>Идентификатор пользователя из системы (заполнится при подключении auth).</summary>
    public string? UserId { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public int TotalRows { get; set; }
    public int SuccessRows { get; set; }
    public int ErrorRows { get; set; }

    /// <summary>Текст ошибки уровня сессии (например, не удалось распарсить файл вообще).</summary>
    public string? ErrorMessage { get; set; }

    // Навигационные свойства
    public List<ImportSessionStage> Stages { get; set; } = [];
    public List<StagedRow> Rows { get; set; } = [];
    public List<ImportError> Errors { get; set; } = [];
    public ImportFileSnapshot? FileSnapshot { get; set; }
}
