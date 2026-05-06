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
///   • «Тип отделки»  → FinishingMaterial (FinishingMaterialId)
///   • «Класс жилья»  → EstateClass        (EstateClassId, в Visary называется «Класс недвижимости»)
///
/// Справочники подтягиваются динамически из Visary
/// (<see cref="IListViewClient.ListFinishingMaterialsAsync"/>,
///  <see cref="IListViewClient.ListEstateClassesAsync"/>) — Title → ID lookup
/// case-insensitive по живым данным справочника. Хардкод-фолбэков нет: если справочник
/// недоступен — file-level error, чтобы не записать неправильные ID.
/// </summary>
public sealed class FinModelImportMapper : IImportMapper
{
    public string ImportTypeCode => "finmodel";

    /// <summary>
    /// Шаблон «Финмодель» — вертикальный key-value layout:
    ///   • лист «Inputs», колонка C — название параметра, колонки H+ — значения по этапам;
    ///   • количество этапов задаётся на листе «Control» в строке параметра
    ///     «Выбрать количество этапов» (имя в столбце F, значение в столбце G).
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

    private static readonly string[] FinishingTypeAliases =
        ["Тип отделки", "FinishingType", "Finishing"];

    // «Класс жилья» в шаблоне = «Класс недвижимости» (EstateClass) на стороне Visary.
    private static readonly string[] EstateClassAliases =
        ["Класс жилья", "EstateClass", "Класс недвижимости"];

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

        // Тянем оба справочника один раз на сессию. Без них валидировать нечем →
        // file-level dictionary_unavailable. Хардкод-фолбэки запрещены — иначе
        // запишем неправильные ID при недоступности Visary.
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

        // Pre-flight: ищем целевые колонки один раз на уровне всего файла.
        // Sparse-строки: агрегируем ключи всех строк через case-insensitive Distinct.
        var allColumns = rows
            .SelectMany(r => r.Cells.Keys)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileFinishingCol = FindColumn(allColumns, FinishingTypeAliases);
        var fileEstateCol    = FindColumn(allColumns, EstateClassAliases);

        if (fileFinishingCol is null && fileEstateCol is null)
        {
            // Ни одной целевой колонки — пользователь явно загрузил не тот шаблон.
            // Отдаём ОДНУ file-level ошибку со списком обнаруженных колонок.
            fileErrors.Add(BuildColumnNotFoundError(allColumns,
                FinishingTypeAliases.Concat(EstateClassAliases).ToArray(),
                "Не найдены колонки 'Тип отделки' и 'Класс жилья'"));
            _log.LogWarning("FinModelImportMapper.ValidateAsync: target columns not found. Detected: {Detected}",
                string.Join(", ", allColumns));
            return new ValidationResult([], fileErrors);
        }

        if (fileFinishingCol is null)
            fileErrors.Add(BuildColumnNotFoundError(allColumns, FinishingTypeAliases,
                "Не найдена колонка 'Тип отделки'"));
        if (fileEstateCol is null)
            fileErrors.Add(BuildColumnNotFoundError(allColumns, EstateClassAliases,
                "Не найдена колонка 'Класс жилья'"));
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

            var finishingEntry = ResolveValue(
                row, fileFinishingCol!, FinishingTypeAliases, finishingByTitle,
                "Тип отделки", allowedFinishing, rowErrors);

            var estateEntry = ResolveValue(
                row, fileEstateCol!, EstateClassAliases, estateByTitle,
                "Класс жилья", allowedEstate, rowErrors);

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

        // Все валидные строки несут одни и те же значения параметров (KeyValueVertical:
        // строки = этапы, параметры одинаковы). Берём первую и применяем оба обновления.
        var firstRow = validRows[0];
        var root = firstRow.MappedValues.RootElement;
        var siteId = context.VisarySiteId.Value;
        var finishingMaterialId = root.GetProperty("FinishingMaterialId").GetInt32();
        var estateClassId       = root.GetProperty("EstateClassId").GetInt32();

        try
        {
            await _visaryClient.UpdateSiteFinishingMaterialAsync(siteId, finishingMaterialId, ct);
            await _visaryClient.UpdateSiteEstateClassAsync(siteId, estateClassId, ct);

            _log.LogInformation(
                "FinModelImportMapper.ApplyAsync: SiteId={SiteId} FinishingMaterialId={FinishingMaterialId} EstateClassId={EstateClassId} success",
                siteId, finishingMaterialId, estateClassId);

            return new ApplyResult(1, errors);
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

    // ─── Helpers ─────────────────────────────────────────────────────────────

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

    private static (int Id, string Title)? ResolveValue(
        ParsedRow row,
        string fileColumn,
        string[] aliases,
        IReadOnlyDictionary<string, (int Id, string Title)> dict,
        string humanName,
        string allowedTitles,
        List<RowError> rowErrors)
    {
        // Per-row fallback на случай sparse-ячеек: ключа может не быть в Cells этой строки.
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

        if (!dict.TryGetValue(value, out var entry))
        {
            rowErrors.Add(new RowError(col, "invalid_value",
                $"Неизвестное значение '{humanName}': '{value}'. Допустимые: {allowedTitles}."));
            return null;
        }

        return entry;
    }
}
