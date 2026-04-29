using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Стратегия валидации и применения для конкретного типа импорта (rooms, shareAgreements, …).
/// Один маппер = один <see cref="ImportTypeCode"/>.
///
/// Жизненный цикл одной сессии:
///   1. <see cref="ValidateAsync"/> — превращает <see cref="ParsedRow"/> в проверенные mapped-значения.
///   2. <see cref="ApplyAsync"/> — записывает валидные строки в visary_db в одной транзакции.
/// </summary>
public interface IImportMapper
{
    /// <summary>Код типа импорта из реестра (rooms / shareAgreements / …).</summary>
    string ImportTypeCode { get; }

    /// <summary>
    /// Проверить распарсенные строки и подготовить mapped-значения.
    /// <paramref name="visaryDb"/> используется только для чтения справочников
    /// (RoomKind, проверка существования Site и т.п.) — НЕ для записи.
    /// </summary>
    Task<ValidationResult> ValidateAsync(
        ImportContext context,
        IReadOnlyList<ParsedRow> rows,
        VisaryDbContext visaryDb,
        CancellationToken ct
    );

    /// <summary>
    /// Применить mapped-значения в целевую БД <paramref name="visaryDb"/> в одной транзакции.
    /// Возвращает количество успешно записанных строк и ошибки уровня apply (FK, unique violations).
    /// </summary>
    Task<ApplyResult> ApplyAsync(
        ImportContext context,
        VisaryDbContext visaryDb,
        IReadOnlyList<MappedRow> rows,
        CancellationToken ct
    );
}

/// <summary>Контекст одной сессии импорта (нужен мапперам для projectId/siteId/userId).</summary>
public record ImportContext(
    Guid SessionId,
    int? VisaryProjectId,
    int? VisarySiteId,
    string? UserId
);

/// <summary>Результат валидации одной строки.</summary>
public record MappedRow(
    int SourceRowNumber,
    bool IsValid,
    System.Text.Json.JsonDocument MappedValues,
    IReadOnlyList<RowError> Errors
);

public record RowError(string? ColumnName, string ErrorCode, string Message);

public record ValidationResult(IReadOnlyList<MappedRow> Rows, IReadOnlyList<RowError> FileLevelErrors);

public record ApplyResult(int AppliedCount, IReadOnlyList<RowError> Errors);
