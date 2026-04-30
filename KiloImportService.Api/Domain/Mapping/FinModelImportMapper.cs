using System.Text.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Маппер импорта типа "Финмодель" (finmodel).
/// Обновляет параметры объекта строительства на основе данных из Excel.
/// 
/// Поддерживаемые параметры:
/// - "Тип отделки" → ConstructionSite.FinishingMaterialId
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

    public FinModelImportMapper(ILogger<FinModelImportMapper> log)
    {
        _log = log;
    }

    public async Task<ValidationResult> ValidateAsync(
        ImportContext context,
        IReadOnlyList<ParsedRow> rows,
        VisaryDbContext visaryDb,
        CancellationToken ct)
    {
        var fileErrors = new List<RowError>();

        if (context.VisarySiteId is null)
        {
            fileErrors.Add(new RowError(null, "site_required",
                "Для импорта финмодели необходимо выбрать объект строительства (Site)."));
            return new ValidationResult([], fileErrors);
        }

        // Проверяем существование выбранного site
        var site = await visaryDb.ConstructionSites
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == context.VisarySiteId.Value && !s.Hidden, ct);

        if (site is null)
        {
            fileErrors.Add(new RowError(null, "site_not_found",
                $"Объект строительства с ID={context.VisarySiteId} не найден или скрыт."));
            return new ValidationResult([], fileErrors);
        }

        var mappedRows = new List<MappedRow>(rows.Count);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var rowErrors = new List<RowError>();

            // Ищем колонку "Тип отделки"
            var finishingTypeCol = row.Cells.Keys.FirstOrDefault(k =>
                FinishingTypeAliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase))
            );

            if (finishingTypeCol is null)
            {
                rowErrors.Add(new RowError(string.Join(" / ", FinishingTypeAliases), "column_not_found",
                    "Не найдена колонка 'Тип отделки'."));
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

        try
        {
            // Получаем объект строительства
            var site = await visaryDb.ConstructionSites
                .FirstOrDefaultAsync(s => s.Id == context.VisarySiteId.Value && !s.Hidden, ct);

            if (site is null)
            {
                errors.Add(new RowError(null, "site_not_found",
                    $"Объект строительства с ID={context.VisarySiteId} не найден или скрыт."));
                return new ApplyResult(0, errors);
            }

            _log.LogInformation(
                "Обновление объекта строительства {SiteId}: FinishingMaterialId={FinishingMaterialId}",
                context.VisarySiteId.Value, finishingMaterialId);

            // Обновляем FinishingMaterialId
            site.FinishingMaterialId = finishingMaterialId;

            await visaryDb.SaveChangesAsync(ct);

            _log.LogInformation(
                "✓ Объект строительства {SiteId} успешно обновлён: FinishingMaterialId={FinishingMaterialId}",
                context.VisarySiteId.Value, finishingMaterialId);

            return new ApplyResult(1, errors);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка при обновлении объекта строительства {SiteId}", context.VisarySiteId);
            errors.Add(new RowError(null, "apply_failed", $"Ошибка обновления: {ex.Message}"));
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
