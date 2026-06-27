using KiloImportService.Api.Domain.Importing;

namespace KiloImportService.Api.Domain.Mapping;

// Файл выделен из IImportMapper.cs (см. doc 140 — Jenkins CS0246 на SyntheticStagedRow):
// типы данных Result-блока маппера — в одном файле с именем = имени основного типа.
// Защита от stale-checkout: даже если SCM-плагин подхватит частично-несвежий
// IImportMapper.cs (например, после переключения веток через `git checkout` без
// предварительного fetch), SyntheticStagedRow остаётся доступным через этот файл.
// На поведение и публичный API изменение не влияет (тот же namespace).

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
