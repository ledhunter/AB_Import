using System.Text.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;
using Visary.Api.CRUD;
using ConstructionSiteRaw = Visary.Api.Dto.ConstructionSiteRaw;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Маппер импорта типа "Финмодель" (finmodel).
/// Обновление типа отделки объекта строительства через Visary CRUD API.
/// 
/// Поддерживаемые параметры:
/// - "Тип отделки" → обновление FinishingMaterialId через Visary API
/// 
/// Справочник "Тип отделки":
/// - "Черновая" → ID=3
/// - "Предчистовая" → ID=2
/// - "Чистовая" → ID=1
/// </summary>
public sealed class FinModelImportMapper : IImportMapper
{
    public string ImportTypeCode => "finmodel";

    private static readonly string[] FinishingTypeAliases = ["Тип отделки", "FinishingType", "Finishing"];

    private readonly ILogger<FinModelImportMapper> _log;
    private readonly ICrudClient _visaryClient;

    public FinModelImportMapper(
        ILogger<FinModelImportMapper> log,
        ICrudClient visaryClient)
    {
        _log = log;
        _visaryClient = visaryClient;
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

            // Проверяем, что значение соответствует справочнику
            var finishingMaterialId = GetFinishingMaterialId(finishingTypeValue);
            if (finishingMaterialId is null)
            {
                rowErrors.Add(new RowError(finishingTypeCol, "invalid_value",
                    $"Неизвестный тип отделки: '{finishingTypeValue}'. Допустимые: Черновая, Предчистовая, Чистовая."));
                mappedRows.Add(new MappedRow(row.SourceRowNumber, false, JsonDocument.Parse("{}"), rowErrors));
                continue;
            }

            // Формируем mapped-значения
            var mappedJson = JsonSerializer.Serialize(new
            {
                FinishingMaterialId = finishingMaterialId.Value,
                FinishingMaterialTitle = finishingTypeValue
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

    /// <summary>
    /// Справочник "Тип отделки" (FinishingMaterial).
    /// Маппинг название → ID.
    /// </summary>
    private static int? GetFinishingMaterialId(string title)
    {
        return title.Trim() switch
        {
            "Черновая" => 3,
            "Предчистовая" => 2,
            "Чистовая" => 1,
            _ => null
        };
    }
}
