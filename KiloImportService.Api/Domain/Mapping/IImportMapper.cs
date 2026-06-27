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
    /// Раскладка файла, ожидаемая этим маппером. Пайплайн пробрасывает её в
    /// <see cref="IFileParser.ParseAsync"/>. По умолчанию — табличная.
    /// </summary>
    FileLayoutHint LayoutHint => FileLayoutHint.Default;

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
/// <param name="SecondaryFileRelativePath">
/// Относительный путь второго (опционального) файла в <c>IFileStorage</c>. На сегодня
/// используется только FinModel-маппером — он по нему открывает файл «План» для
/// чтения краевых квартальных значений и создания <c>fmmodel</c> в Visary
/// (см. doc 110). Для остальных импортов остаётся <c>null</c>.
/// </param>
/// <param name="PrimaryFileRelativePath">
/// Относительный путь основного файла импорта в <c>IFileStorage</c>. Заполняется
/// пайплайном из <c>session.FileSnapshot.RelativePath</c>. FinModel-маппер использует
/// его, чтобы открыть лист <c>Outputs</c> и подтянуть Fact-блок («Доходы поэтапно» →
/// «Этап 1») в существующую версию Финмодели поверх плановых InputData (см. doc 126).
/// </param>
public record ImportContext(
    Guid SessionId,
    int? VisaryProjectId,
    int? VisarySiteId,
    string? UserId,
    string? SecondaryFileRelativePath = null,
    string? PrimaryFileRelativePath = null
);

/// <summary>Результат валидации одной строки.</summary>
/// <remarks>
/// <c>Sheet</c> — имя листа Excel, из которого пришла строка. Маппер ОБЯЗАН его
/// заполнять: пайплайн использует этот лист (а не индекс в parseResult) для
/// записи <c>StagedRow.Sheet</c>. Раньше пайплайн брал <c>Sheet</c> по индексу
/// <c>parseResult.Rows[i].Sheet</c>, но маппер может «тихо» пропускать строки
/// (например, сводные «ИТОГО» без НПС/Этапа), отчего индексы расходятся и в БД
/// падает уникальный индекс <c>(SessionId, Sheet, SourceRowNumber)</c>.
/// </remarks>
public record MappedRow(
    int SourceRowNumber,
    string Sheet,
    bool IsValid,
    System.Text.Json.JsonDocument MappedValues,
    IReadOnlyList<RowError> Errors
);

/// <param name="SourceRowNumber">
/// Абсолютный номер строки в исходном файле. Для file-level ошибок (на этапе
/// Validate — отсутствие колонок, неверный лист и т.п.) оставлять <c>null</c>:
/// Pipeline запишет 0, фронт отрисует в блоке «Ошибки уровня файла».
/// Для Apply-ошибок, относящихся к конкретной строке, передавать абсолютный
/// row-номер (берётся из <see cref="MappedRow.SourceRowNumber"/>) — фронт
/// сгруппирует ошибку по <c>(Sheet, RowNumber)</c> и покажет внутри таблицы.
/// </param>
/// <param name="Sheet">
/// Имя листа, к которому относится ошибка. Используется в паре с
/// <paramref name="SourceRowNumber"/>. <c>null</c> для file-level ошибок.
/// </param>
public record RowError(
    string? ColumnName, string ErrorCode, string Message,
    int? SourceRowNumber = null, string? Sheet = null);

public record ValidationResult(IReadOnlyList<MappedRow> Rows, IReadOnlyList<RowError> FileLevelErrors);

public record ApplyResult(
    int AppliedCount,
    IReadOnlyList<RowError> Errors,
    IReadOnlyList<RowActionLog>? RowActions = null,
    IReadOnlyList<SyntheticStagedRow>? SyntheticRows = null);

// `SyntheticStagedRow` и `RowActionLog` вынесены в отдельный файл
// `SyntheticStagedRow.cs` (см. doc 140) — это страхует от Jenkins CS0246
// на stale-checkout'е, когда `IImportMapper.cs` мог подхватиться от другой
// ветки без типа `SyntheticStagedRow`, а `FinModelImportMapper.cs` уже его
// использовал. На namespace и API изменение не влияет.
