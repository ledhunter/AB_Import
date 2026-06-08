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

/// <summary>
/// «Виртуальная» строка отчёта — для операций, которые мапер выполняет ВНЕ цикла
/// по обычным <see cref="MappedRow"/> (например, FinModel создаёт fmmodel, fmmodelversion,
/// inputdata по плану и факту, организации, deal pre-check, бюджет, ГФ — всё это
/// идёт прямо в Visary CRUD API, минуя парсер/staged_rows). Чтобы пользователь видел
/// эти операции в отчёте как обычные строки, мапер возвращает их в <c>SyntheticRows</c>,
/// а Pipeline инсертит каждую как <see cref="StagedRow"/> с указанными <see cref="Sheet"/>/<see cref="SourceRowNumber"/>.
/// См. doc 128.
/// </summary>
/// <param name="Sheet">
/// Имя «синтетического листа» (например, «Финмодель», «План — Общий график»,
/// «Outputs — Факт», «Бюджет ИСР»). Группировка в отчёте идёт по нему же.
/// Не должен пересекаться с реальными именами листов из ParsedRow.
/// </param>
/// <param name="SourceRowNumber">
/// Порядковый номер строки в пределах синтетического листа (1..N). Должен быть
/// уникален в этой группе — иначе сломается unique-index (SessionId, Sheet, SourceRowNumber).
/// </param>
/// <param name="Status">
/// Логический статус операции: <c>Applied</c> (успех), <c>Failed</c> (упало),
/// <c>Invalid</c> (валидация бизнес-правил не прошла).
/// </param>
/// <param name="Actions">
/// Action-логи (как в <see cref="RowActionLog"/>): одна-две лаконичные человекочитаемые
/// метки — «Финмодель создана id=48», «InputData [2026Q1, Квартиры (план)] создана».
/// Бизнес-язык — никакого PATCH/POST/имён DTO (см. doc 125).
/// </param>
/// <param name="MappedValuesJson">
/// Опциональный JSON с распарсенными значениями для UI («что было передано в Visary»):
/// например, для inputdata — <c>{"FmPeriod":"2026Q1","Code":"010","Summ":...,"Amount":...,"Cost":...}</c>.
/// <c>null</c> → пустой <c>{}</c>.
/// </param>
public record SyntheticStagedRow(
    string Sheet,
    int SourceRowNumber,
    StagedRowStatus Status,
    IReadOnlyList<string> Actions,
    string? MappedValuesJson = null);

/// <summary>
/// Журнал реальных действий, выполненных по одной строке файла в Apply-фазе.
/// Маппер заполняет (опционально) список лаконичных русскоязычных меток —
/// «Корпус создан», «Помещение обновлено», «ДДУ найден (не создан)», «ДДУ
/// привязан к новому помещению», «Застройщик переиспользован», … — чтобы
/// в построчном отчёте было видно, ЧТО реально произошло, а не только статус.
///
/// Pipeline сериализует это в <c>StagedRow.Actions</c> (JSON-массив), отдаёт
/// в <c>GetReport</c>, UI отрисовывает рядом со строкой.
/// </summary>
/// <param name="SourceRowNumber">Абсолютный номер строки в исходном файле (Excel-row).</param>
/// <param name="Sheet">Имя листа, в котором эта строка (для многолистовых файлов).</param>
/// <param name="Actions">Список меток действий в порядке выполнения.</param>
public record RowActionLog(int SourceRowNumber, string Sheet, IReadOnlyList<string> Actions);
