using System.Globalization;
using System.Text.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Маппер импорта типа «Финмодель» (finmodel).
/// Обновление параметров объекта строительства через Visary CRUD API.
///
/// Поддерживаемые параметры:
///   • «Тип отделки»       → FinishingMaterial   (FK на Site)
///   • «Класс жилья»       → EstateClass         (FK на Site, в Visary «Класс недвижимости»)
///   • «Площадь застройки» → ConstructionSiteIndicator + ConstructionSiteIndicatorValue
///                            с конкретной стадией (Stage = 50 «Экспертиза»)
///
/// Справочники подтягиваются динамически из Visary (Title → ID lookup case-insensitive).
/// Хардкод-фолбэков нет: если справочник недоступен — file-level error.
/// </summary>
public sealed class FinModelImportMapper : IImportMapper
{
    public string ImportTypeCode => "finmodel";

    /// <summary>
    /// Шаблон «Финмодель» — вертикальный key-value layout:
    ///   • лист «Inputs», колонка C — название параметра, колонки H+ — значения по этапам;
    ///   • количество этапов задаётся на листе «Control» в строке параметра
    ///     «Выбрать количество этапов» (имя в столбце F, значение в столбце G).
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

    private static readonly string[] FinishingTypeAliases =
        ["Тип отделки", "FinishingType", "Finishing"];

    // «Класс жилья» в шаблоне = «Класс недвижимости» (EstateClass) на стороне Visary.
    private static readonly string[] EstateClassAliases =
        ["Класс жилья", "EstateClass", "Класс недвижимости"];

    // Domain.Model.Enums.ProjectStage: 50 = Expertise (Экспертиза).
    // Источник: FinModel/Альфа Банк. Управление проектами.drawio.xml — диаграмма enum'а.
    private const int ProjectStageExpertise = 50;
    private const string ExpertiseHumanName = "Экспертиза";

    // Декларативный список indicator-параметров. Добавление нового показателя =
    // одна строка в массиве (не нужно трогать flow). Title должен совпадать с тем,
    // как Visary хранит показатель (см. listview/constructionsiteindicator).
    private static readonly IndicatorParameter[] Indicators =
    [
        new(
            HumanName:     "Площадь застройки",
            Aliases:       ["Площадь застройки", "BuildingArea"],
            VisaryTitle:   "Площадь застройки",
            Stage:         ProjectStageExpertise),
        new(
            HumanName:     "Плотность застройки",
            Aliases:       ["Плотность застройки", "BuildingDensity"],
            VisaryTitle:   "Плотность застройки",
            Stage:         ProjectStageExpertise),
    ];

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
        _log.LogInformation("FinModelImportMapper.ValidateAsync: siteId={SiteId}, rows={RowCount}",
            context.VisarySiteId, rows.Count);

        var fileErrors = new List<RowError>();

        if (context.VisarySiteId is null)
        {
            fileErrors.Add(new RowError(null, "site_required",
                "Для импорта финмодели необходимо выбрать объект строительства (Site)."));
            return new ValidationResult([], fileErrors);
        }

        var site = await visaryDb.ConstructionSites
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == context.VisarySiteId.Value && !s.Hidden, ct);
        if (site is null)
        {
            fileErrors.Add(new RowError(null, "site_not_found",
                $"Объект строительства с ID={context.VisarySiteId} не найден или скрыт."));
            return new ValidationResult([], fileErrors);
        }

        // Тянем оба справочника один раз на сессию.
        var finishingByTitle = await TryLoadDictionaryAsync(
            "Тип отделки",
            ct => _listViewClient.ListFinishingMaterialsAsync(ct),
            m => m.ID, m => m.Title,
            fileErrors, ct);
        if (finishingByTitle is null)
            return new ValidationResult([], fileErrors);

        var estateByTitle = await TryLoadDictionaryAsync(
            "Класс недвижимости",
            ct => _listViewClient.ListEstateClassesAsync(ct),
            m => m.ID, m => m.Title,
            fileErrors, ct);
        if (estateByTitle is null)
            return new ValidationResult([], fileErrors);

        // Pre-flight колонок.
        var allColumns = rows
            .SelectMany(r => r.Cells.Keys)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileFinishingCol = FindColumn(allColumns, FinishingTypeAliases);
        var fileEstateCol    = FindColumn(allColumns, EstateClassAliases);
        var indicatorCols    = Indicators
            .Select(p => (Param: p, Col: FindColumn(allColumns, p.Aliases)))
            .ToArray();

        // Если НИ ОДНОЙ целевой колонки нет — пользователь явно загрузил не тот шаблон.
        var anyFound = fileFinishingCol is not null
                       || fileEstateCol is not null
                       || indicatorCols.Any(x => x.Col is not null);

        if (!anyFound)
        {
            var allAliases = FinishingTypeAliases
                .Concat(EstateClassAliases)
                .Concat(Indicators.SelectMany(p => p.Aliases))
                .ToArray();
            fileErrors.Add(BuildColumnNotFoundError(allColumns, allAliases,
                "Не найдены целевые колонки шаблона 'Финмодель'"));
            _log.LogWarning("FinModelImportMapper.ValidateAsync: no target columns found. Detected: {Detected}",
                string.Join(", ", allColumns));
            return new ValidationResult([], fileErrors);
        }

        // Какая-то колонка нашлась, но не все — отдельная file-level ошибка на каждую.
        if (fileFinishingCol is null)
            fileErrors.Add(BuildColumnNotFoundError(allColumns, FinishingTypeAliases,
                "Не найдена колонка 'Тип отделки'"));
        if (fileEstateCol is null)
            fileErrors.Add(BuildColumnNotFoundError(allColumns, EstateClassAliases,
                "Не найдена колонка 'Класс жилья'"));
        foreach (var (param, col) in indicatorCols)
        {
            if (col is null)
                fileErrors.Add(BuildColumnNotFoundError(allColumns, param.Aliases,
                    $"Не найдена колонка '{param.HumanName}'"));
        }
        if (fileErrors.Count > 0)
            return new ValidationResult([], fileErrors);

        var allowedFinishing = string.Join(", ", finishingByTitle.Values.Select(v => v.Title));
        var allowedEstate    = string.Join(", ", estateByTitle.Values.Select(v => v.Title));

        var mappedRows = new List<MappedRow>(rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            var rowErrors = new List<RowError>();

            if (i % 500 == 0 || i == rows.Count - 1)
                _log.LogInformation("FinModelImportMapper.ValidateAsync: processing row {Current}/{Total}", i + 1, rows.Count);

            var finishingEntry = ResolveDictionaryValue(
                row, fileFinishingCol!, FinishingTypeAliases, finishingByTitle,
                "Тип отделки", allowedFinishing, rowErrors);

            var estateEntry = ResolveDictionaryValue(
                row, fileEstateCol!, EstateClassAliases, estateByTitle,
                "Класс жилья", allowedEstate, rowErrors);

            var indicatorValues = new Dictionary<string, double>();
            foreach (var (param, col) in indicatorCols)
            {
                var v = ResolveDoubleValue(row, col!, param.Aliases, param.HumanName, rowErrors);
                if (v.HasValue) indicatorValues[param.HumanName] = v.Value;
            }

            if (rowErrors.Count > 0)
            {
                mappedRows.Add(new MappedRow(row.SourceRowNumber, false, JsonDocument.Parse("{}"), rowErrors));
                continue;
            }

            var mappedJson = JsonSerializer.Serialize(new
            {
                FinishingMaterialId    = finishingEntry!.Value.Id,
                FinishingMaterialTitle = finishingEntry.Value.Title,
                EstateClassId          = estateEntry!.Value.Id,
                EstateClassTitle       = estateEntry.Value.Title,
                Indicators             = indicatorValues,
            });

            mappedRows.Add(new MappedRow(
                row.SourceRowNumber, true, JsonDocument.Parse(mappedJson), rowErrors));
        }

        _log.LogInformation("FinModelImportMapper.ValidateAsync: completed mappedRows={Count} fileErrors={FileErrorCount}",
            mappedRows.Count, fileErrors.Count);
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

        var firstRow = validRows[0];
        var root = firstRow.MappedValues.RootElement;
        var siteId = context.VisarySiteId.Value;
        var finishingMaterialId = root.GetProperty("FinishingMaterialId").GetInt32();
        var estateClassId       = root.GetProperty("EstateClassId").GetInt32();

        try
        {
            await _visaryClient.UpdateSiteFinishingMaterialAsync(siteId, finishingMaterialId, ct);
            await _visaryClient.UpdateSiteEstateClassAsync(siteId, estateClassId, ct);

            // Indicator-параметры: для каждого находим показатель → конкретное значение
            // нужной стадии → PATCH. Каждый параметр обрабатывается независимо;
            // один сбой не отменяет уже применённые обновления (не транзакционно).
            if (root.TryGetProperty("Indicators", out var indicatorsJson))
            {
                foreach (var (param, value) in EnumerateIndicators(indicatorsJson))
                {
                    try
                    {
                        await ApplyIndicatorAsync(siteId, param, value, ct);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        _log.LogError(ex,
                            "Indicator '{Param}' not found for siteId={SiteId}", param.HumanName, siteId);
                        errors.Add(new RowError(null, "indicator_not_found", ex.Message));
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Indicator '{Param}' update failed for siteId={SiteId}", param.HumanName, siteId);
                        errors.Add(new RowError(null, "indicator_update_error",
                            $"Ошибка обновления показателя '{param.HumanName}': {ex.Message}"));
                    }
                }
            }

            _log.LogInformation(
                "FinModelImportMapper.ApplyAsync: SiteId={SiteId} FinishingMaterialId={Fm} EstateClassId={Ec} indicators={Indicators}",
                siteId, finishingMaterialId, estateClassId, Indicators.Length);

            return new ApplyResult(errors.Count == 0 ? 1 : 0, errors);
        }
        catch (KeyNotFoundException ex)
        {
            _log.LogError(ex, "Visary site not found for siteId={SiteId}", siteId);
            errors.Add(new RowError(null, "visary_site_not_found",
                $"Объект строительства {siteId} не найден в Visary."));
            return new ApplyResult(0, errors);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Visary update failed for siteId={SiteId}", siteId);
            errors.Add(new RowError(null, "visary_update_error",
                $"Ошибка обновления в Visary: {ex.Message}"));
            return new ApplyResult(0, errors);
        }
    }

    // ─── Indicator flow ──────────────────────────────────────────────────────

    private async Task ApplyIndicatorAsync(int siteId, IndicatorParameter param, double value, CancellationToken ct)
    {
        // 1. Найти показатель (ConstructionSiteIndicator) на сайте.
        //    GetIndicatorsBySiteAsync использует Filter ["Title","contains",X] — на сервере
        //    отбираются записи, содержащие подстроку. Это нужно потому, что Title в Visary
        //    может содержать хвостовые пробелы ("Площадь застройки "). Точное совпадение
        //    делаем уже здесь — Trim()+OrdinalIgnoreCase, чтобы не словить «Общая площадь застройки».
        var indicators = await _listViewClient.GetIndicatorsBySiteAsync(siteId, param.VisaryTitle, ct);
        var needle = param.VisaryTitle.Trim();
        var indicator = indicators.Data.FirstOrDefault(i =>
            string.Equals(i.Title?.Trim(), needle, StringComparison.OrdinalIgnoreCase));
        if (indicator is null)
            throw new KeyNotFoundException(
                $"Показатель '{param.VisaryTitle}' не найден у объекта siteId={siteId}.");

        // 2. Среди значений показателя найти запись с нужной Stage (Экспертиза = 50).
        var values = await _listViewClient.GetIndicatorValuesByIndicatorAsync(indicator.ID, ct);
        var target = values.Data.FirstOrDefault(v => v.Stage == param.Stage);
        if (target is null)
            throw new KeyNotFoundException(
                $"У показателя '{param.VisaryTitle}' (id={indicator.ID}) нет значения со стадией {param.Stage} ({ExpertiseHumanName}).");

        // 3. GET /crud/.../{id} — нужен актуальный RowVersion (long). В listview Version — DateTime,
        //    она для PATCH не подходит. Тот же паттерн, что в UpdateSiteFinishingMaterialAsync (doc 63).
        var current = await _visaryClient.GetIndicatorValueByIdAsync(target.ID, ct);

        // 4. PATCH — обновляем только Value, RowVersion для optimistic locking.
        await _visaryClient.PatchIndicatorValueAsync(target.ID, new IndicatorValuePatchRequest
        {
            ID         = target.ID,
            RowVersion = current.RowVersion,
            Value      = value,
        }, ct);

        _log.LogInformation(
            "Indicator '{Param}' (indicatorId={IndicatorId}, valueId={ValueId}, Stage={Stage}) updated to {Value}",
            param.HumanName, indicator.ID, target.ID, param.Stage, value);
    }

    private static IEnumerable<(IndicatorParameter Param, double Value)> EnumerateIndicators(JsonElement indicatorsJson)
    {
        if (indicatorsJson.ValueKind != JsonValueKind.Object) yield break;
        foreach (var param in Indicators)
        {
            if (indicatorsJson.TryGetProperty(param.HumanName, out var v)
                && v.ValueKind == JsonValueKind.Number)
            {
                yield return (param, v.GetDouble());
            }
        }
    }

    // ─── Generic helpers ─────────────────────────────────────────────────────

    private async Task<Dictionary<string, (int Id, string Title)>?> TryLoadDictionaryAsync<T>(
        string humanName,
        Func<CancellationToken, Task<ListViewResponse<T>>> loader,
        Func<T, int> idSelector,
        Func<T, string?> titleSelector,
        List<RowError> fileErrors,
        CancellationToken ct)
    {
        try
        {
            var resp = await loader(ct);
            var dict = resp.Data
                .Where(m => !string.IsNullOrWhiteSpace(titleSelector(m)))
                .ToDictionary(
                    m => titleSelector(m)!.Trim(),
                    m => (idSelector(m), titleSelector(m)!.Trim()),
                    StringComparer.OrdinalIgnoreCase);
            _log.LogInformation("FinModelImportMapper: dictionary '{Name}' loaded ({Count} entries)", humanName, dict.Count);

            if (dict.Count == 0)
            {
                fileErrors.Add(new RowError(null, "dictionary_empty",
                    $"Справочник «{humanName}» в Visary пуст — нечего сопоставлять."));
                return null;
            }
            return dict;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FinModelImportMapper: failed to load '{Name}' dictionary", humanName);
            fileErrors.Add(new RowError(null, "dictionary_unavailable",
                $"Не удалось получить справочник «{humanName}» из Visary: {ex.Message}"));
            return null;
        }
    }

    private static string? FindColumn(IReadOnlyList<string> allColumns, string[] aliases)
        => allColumns.FirstOrDefault(k =>
            aliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase)));

    private static RowError BuildColumnNotFoundError(
        IReadOnlyList<string> allColumns, string[] aliases, string headline)
    {
        var detectedList = allColumns.Count == 0
            ? "(колонки не найдены)"
            : string.Join(", ", allColumns.Take(20).Select(c => $"'{c}'"))
              + (allColumns.Count > 20 ? $" и ещё {allColumns.Count - 20}…" : string.Empty);

        return new RowError(
            string.Join(" / ", aliases),
            "column_not_found",
            $"{headline} (допустимые алиасы: {string.Join(", ", aliases)}). " +
            $"В файле обнаружены колонки: {detectedList}. " +
            "Убедитесь, что вы загружаете шаблон импорта 'Финмодель'.");
    }

    private static (int Id, string Title)? ResolveDictionaryValue(
        ParsedRow row,
        string fileColumn,
        string[] aliases,
        IReadOnlyDictionary<string, (int Id, string Title)> dict,
        string humanName,
        string allowedTitles,
        List<RowError> rowErrors)
    {
        var value = ReadCellTrimmed(row, fileColumn, aliases, humanName, rowErrors);
        if (value is null) return null;

        if (!dict.TryGetValue(value, out var entry))
        {
            rowErrors.Add(new RowError(fileColumn, "invalid_value",
                $"Неизвестное значение '{humanName}': '{value}'. Допустимые: {allowedTitles}."));
            return null;
        }
        return entry;
    }

    private static double? ResolveDoubleValue(
        ParsedRow row,
        string fileColumn,
        string[] aliases,
        string humanName,
        List<RowError> rowErrors)
    {
        var value = ReadCellTrimmed(row, fileColumn, aliases, humanName, rowErrors);
        if (value is null) return null;

        if (!TryParseFlexibleDouble(value, out var d))
        {
            rowErrors.Add(new RowError(fileColumn, "invalid_value",
                $"Значение '{humanName}' не является числом: '{value}'."));
            return null;
        }
        return d;
    }

    // Excel может отдать ячейку как "12345.67", "12345,67" или "12 345,67" — пробуем оба.
    private static bool TryParseFlexibleDouble(string raw, out double result)
    {
        var cleaned = raw.Replace(" ", "").Replace(" ", "");
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            return true;
        return double.TryParse(cleaned.Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out result);
    }

    // Возвращает trimmed-значение или null (тогда уже добавлена value_empty в rowErrors).
    private static string? ReadCellTrimmed(
        ParsedRow row, string fileColumn, string[] aliases, string humanName, List<RowError> rowErrors)
    {
        var col = row.Cells.ContainsKey(fileColumn)
            ? fileColumn
            : row.Cells.Keys.FirstOrDefault(k =>
                aliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase)));

        if (col is null)
        {
            rowErrors.Add(new RowError(fileColumn, "value_empty",
                $"Значение '{humanName}' пустое."));
            return null;
        }

        var value = row.Cells[col]?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            rowErrors.Add(new RowError(col, "value_empty",
                $"Значение '{humanName}' пустое."));
            return null;
        }
        return value;
    }

    /// <summary>Декларативное описание indicator-параметра импорта.</summary>
    /// <param name="HumanName">Человекочитаемое имя для логов и ошибок.</param>
    /// <param name="Aliases">Возможные имена колонки в Excel.</param>
    /// <param name="VisaryTitle">Точный Title показателя в Visary (для filter ["Title","=",X]).</param>
    /// <param name="Stage">int-значение Domain.Model.Enums.ProjectStage (50 = Экспертиза).</param>
    private sealed record IndicatorParameter(
        string HumanName,
        string[] Aliases,
        string VisaryTitle,
        int Stage);
}
