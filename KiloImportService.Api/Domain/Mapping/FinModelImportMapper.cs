using System.Text.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;
using Visary.Api.CRUD;
using Visary.Api.ListView;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Маппер импорта типа «Финмодель» (finmodel).
/// Обновление типа отделки объекта строительства через Visary CRUD API.
///
/// Поддерживаемые параметры:
///   • «Тип отделки» → обновление FinishingMaterialId через Visary API.
///
/// Справочник «Тип отделки» подтягивается динамически из Visary
/// (<see cref="IListViewClient.ListFinishingMaterialsAsync"/>) — раньше был хардкод
/// (Черновая=3 / Предчистовая=2 / Чистовая=1), но идентификаторы могут меняться,
/// и там могут появиться новые значения. Теперь Title → ID — case-insensitive
/// lookup по живым данным справочника.
/// </summary>
public sealed class FinModelImportMapper : IImportMapper
{
    public string ImportTypeCode => "finmodel";

    /// <summary>
    /// Шаблон «Финмодель» — вертикальный key-value layout:
    ///   • лист «Inputs», колонка C — название параметра, колонки H+ — значения по этапам;
    ///   • количество этапов (= количество колонок-значений для чтения) задаётся на
    ///     листе «Control» в строке параметра «Выбрать количество этапов» (имя в столбце F,
    ///     значение в столбце G).
    /// Парсер выпускает по одному ParsedRow на каждый этап; маппер видит их как обычные строки.
    /// </summary>
    public FileLayoutHint LayoutHint { get; } = new KeyValueVertical(
        SheetName: "Inputs",
        KeyColumn: "C",
        ValueStartColumn: "H",
        StageCount: new StageCountReference(
            SheetName: "Control",
            KeyColumn: "F",
            ValueColumn: "G",
            ParameterName: "Выбрать количество этапов"));

    private static readonly string[] FinishingTypeAliases = ["Тип отделки", "FinishingType", "Finishing"];

    private readonly ILogger<FinModelImportMapper> _log;
    private readonly ICrudClient _visaryClient;
    private readonly IListViewClient _listViewClient;

    public FinModelImportMapper(
        ILogger<FinModelImportMapper> log,
        ICrudClient visaryClient,
        IListViewClient listViewClient)
    {
        _log = log;
        _visaryClient = visaryClient;
        _listViewClient = listViewClient;
    }

    public async Task<ValidationResult> ValidateAsync(
        ImportContext context,
        IReadOnlyList<ParsedRow> rows,
        VisaryDbContext visaryDb,
        CancellationToken ct)
    {
        _log.LogInformation("FinModelImportMapper.ValidateAsync: siteId={SiteId}, rows={RowCount}", context.VisarySiteId, rows.Count);

        var fileErrors = new List<RowError>();

        if (context.VisarySiteId is null)
        {
            fileErrors.Add(new RowError(null, "site_required",
                "Для импорта финмодели необходимо выбрать объект строительства (Site)."));
            return new ValidationResult([], fileErrors);
        }

        // Проверяем существование выбранного site
        _log.LogInformation("FinModelImportMapper.ValidateAsync: querying ConstructionSite {SiteId}", context.VisarySiteId.Value);
        var site = await visaryDb.ConstructionSites
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == context.VisarySiteId.Value && !s.Hidden, ct);
        _log.LogInformation("FinModelImportMapper.ValidateAsync: ConstructionSite query completed siteFound={SiteFound}", site != null);

        if (site is null)
        {
            fileErrors.Add(new RowError(null, "site_not_found",
                $"Объект строительства с ID={context.VisarySiteId} не найден или скрыт."));
            return new ValidationResult([], fileErrors);
        }

        // Тянем справочник «Тип отделки» из Visary один раз на сессию.
        // Если Visary недоступен — это file-level ошибка (без справочника валидировать нечем).
        Dictionary<string, (int Id, string Title)> finishingByTitle;
        try
        {
            var fm = await _listViewClient.ListFinishingMaterialsAsync(ct);
            finishingByTitle = fm.Data
                .Where(m => !string.IsNullOrWhiteSpace(m.Title))
                .ToDictionary(m => m.Title!.Trim(), m => (m.ID, m.Title!.Trim()),
                    StringComparer.OrdinalIgnoreCase);
            _log.LogInformation("FinModelImportMapper.ValidateAsync: finishingmaterial dictionary loaded ({Count} entries)", finishingByTitle.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FinModelImportMapper.ValidateAsync: failed to load finishingmaterial dictionary");
            fileErrors.Add(new RowError(null, "dictionary_unavailable",
                "Не удалось получить справочник «Тип отделки» из Visary: " + ex.Message));
            return new ValidationResult([], fileErrors);
        }

        if (finishingByTitle.Count == 0)
        {
            fileErrors.Add(new RowError(null, "dictionary_empty",
                "Справочник «Тип отделки» в Visary пуст — нечего сопоставлять."));
            return new ValidationResult([], fileErrors);
        }

        // Pre-flight: ищем целевую колонку один раз на уровне всего файла.
        // Учитываем sparse-строки (Excel может пропускать пустые ячейки): агрегируем
        // ключи всех строк через case-insensitive Distinct.
        var allColumns = rows
            .SelectMany(r => r.Cells.Keys)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileFinishingTypeCol = allColumns.FirstOrDefault(k =>
            FinishingTypeAliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase)));

        if (fileFinishingTypeCol is null)
        {
            // Без целевой колонки делать нечего — отдаём ОДНУ file-level ошибку
            // со списком обнаруженных колонок, чтобы пользователь сразу понял,
            // что загрузил не тот шаблон.
            var detectedList = allColumns.Count == 0
                ? "(колонки не найдены)"
                : string.Join(", ", allColumns.Take(20).Select(c => $"'{c}'"))
                  + (allColumns.Count > 20 ? $" и ещё {allColumns.Count - 20}…" : string.Empty);

            fileErrors.Add(new RowError(
                string.Join(" / ", FinishingTypeAliases),
                "column_not_found",
                $"Не найдена колонка 'Тип отделки' (допустимые алиасы: {string.Join(", ", FinishingTypeAliases)}). " +
                $"В файле обнаружены колонки: {detectedList}. " +
                "Убедитесь, что вы загружаете шаблон импорта 'Финмодель'."));

            _log.LogWarning(
                "FinModelImportMapper.ValidateAsync: column 'Тип отделки' not found in file. Detected columns: {Detected}",
                string.Join(", ", allColumns));

            return new ValidationResult([], fileErrors);
        }

        var allowedTitles = string.Join(", ", finishingByTitle.Values.Select(v => v.Title));
        var mappedRows = new List<MappedRow>(rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            var rowErrors = new List<RowError>();

            if (i % 500 == 0 || i == rows.Count - 1)
            {
                _log.LogInformation("FinModelImportMapper.ValidateAsync: processing row {Current}/{Total}", i + 1, rows.Count);
            }

            // На уровне строки используем тот же ключ, что и на уровне файла,
            // но с fallback'ом на per-row lookup для sparse-строк (где ячейка может
            // отсутствовать в Cells).
            var finishingTypeCol = row.Cells.ContainsKey(fileFinishingTypeCol)
                ? fileFinishingTypeCol
                : row.Cells.Keys.FirstOrDefault(k =>
                    FinishingTypeAliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase)));

            if (finishingTypeCol is null)
            {
                rowErrors.Add(new RowError(fileFinishingTypeCol, "value_empty",
                    "Значение 'Тип отделки' пустое."));
                mappedRows.Add(new MappedRow(row.SourceRowNumber, false, JsonDocument.Parse("{}"), rowErrors));
                continue;
            }

            var finishingTypeValue = row.Cells[finishingTypeCol]?.Trim();
            if (string.IsNullOrWhiteSpace(finishingTypeValue))
            {
                rowErrors.Add(new RowError(finishingTypeCol, "value_empty",
                    "Значение 'Тип отделки' пустое."));
                mappedRows.Add(new MappedRow(row.SourceRowNumber, false, JsonDocument.Parse("{}"), rowErrors));
                continue;
            }

            // Title → ID по живому справочнику Visary (case-insensitive).
            if (!finishingByTitle.TryGetValue(finishingTypeValue, out var dictEntry))
            {
                rowErrors.Add(new RowError(finishingTypeCol, "invalid_value",
                    $"Неизвестный тип отделки: '{finishingTypeValue}'. Допустимые: {allowedTitles}."));
                mappedRows.Add(new MappedRow(row.SourceRowNumber, false, JsonDocument.Parse("{}"), rowErrors));
                continue;
            }

            var mappedJson = JsonSerializer.Serialize(new
            {
                FinishingMaterialId = dictEntry.Id,
                FinishingMaterialTitle = dictEntry.Title,
            });

            mappedRows.Add(new MappedRow(
                row.SourceRowNumber,
                true,
                JsonDocument.Parse(mappedJson),
                rowErrors
            ));
        }

        _log.LogInformation("FinModelImportMapper.ValidateAsync: completed mappedRows={MappedRowCount} fileErrors={FileErrorCount}", mappedRows.Count, fileErrors.Count);
        return new ValidationResult(mappedRows, fileErrors);
    }

    public async Task<ApplyResult> ApplyAsync(
        ImportContext context,
        VisaryDbContext visaryDb,
        IReadOnlyList<MappedRow> rows,
        CancellationToken ct)
    {
        var errors = new List<RowError>();

        if (context.VisarySiteId is null)
        {
            errors.Add(new RowError(null, "site_required",
                "Не указан объект строительства (visarySiteId)."));
            return new ApplyResult(0, errors);
        }

        var validRows = rows.Where(r => r.IsValid).ToList();
        if (validRows.Count == 0)
        {
            _log.LogWarning("Нет валидных строк для применения.");
            return new ApplyResult(0, errors);
        }

        // Берём первую валидную строку (предполагается, что в файле одна строка с параметрами)
        var firstRow = validRows[0];
        var finishingMaterialId = firstRow.MappedValues.RootElement.GetProperty("FinishingMaterialId").GetInt32();

        // Обновление через Visary CRUD API
        try
        {
            var success = await _visaryClient.UpdateSiteFinishingMaterialAsync(
                context.VisarySiteId.Value, finishingMaterialId, ct);

            _log.LogInformation(
                "FinModelImportMapper.ApplyAsync: обновление FinishingMaterialId={FinishingMaterialId} для SiteId={SiteId} успешно",
                finishingMaterialId, context.VisarySiteId.Value);

            return new ApplyResult(1, errors);
        }
        catch (KeyNotFoundException ex)
        {
            _log.LogError(ex, "Visary site not found for siteId={SiteId}", context.VisarySiteId);
            errors.Add(new RowError(null, "visary_site_not_found",
                $"Объект строительства {context.VisarySiteId} не найден в Visary."));
            return new ApplyResult(0, errors);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Visary update failed for siteId={SiteId}", context.VisarySiteId);
            errors.Add(new RowError(null, "visary_update_error",
                $"Ошибка обновления в Visary: {ex.Message}"));
            return new ApplyResult(0, errors);
        }
    }
}
