namespace KiloImportService.Api.Data.Entities;

/// <summary>
/// Ошибка валидации/парсинга/применения, привязанная к конкретной строке файла.
/// </summary>
public class ImportError
{
    public long Id { get; set; }
    public Guid ImportSessionId { get; set; }
    public ImportSession Session { get; set; } = null!;

    /// <summary>Номер строки источника (как в <see cref="StagedRow.SourceRowNumber"/>). 0 = ошибка уровня файла.</summary>
    public int SourceRowNumber { get; set; }

    /// <summary>Имя колонки в исходном файле (если ошибка относится к конкретной колонке).</summary>
    public string? ColumnName { get; set; }

    /// <summary>Машинный код ошибки: <c>parse_failure</c>, <c>required_missing</c>, <c>invalid_number</c>, <c>fk_not_found</c>…</summary>
    public string ErrorCode { get; set; } = null!;

    /// <summary>Человекочитаемое сообщение для UI.</summary>
    public string Message { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
