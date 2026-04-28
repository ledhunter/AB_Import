using System.Globalization;
using System.Text.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Маппер импорта типа <c>rooms</c> (Помещения).
/// Маппит колонки исходного файла → поля сущности <see cref="Room"/>.
///
/// Соответствие колонок (case-insensitive, можно несколько алиасов):
///   "Номер квартиры" / "Номер" / "Number"        → Room.Number
///   "Этаж" / "Floor"                              → Room.Floor
///   "Тип" / "Вид помещения" / "Kind"              → Room.KindId  (поиск по названию в RoomKind)
///   "Площадь по проекту" / "ProjectArea" /
///     "Площадь общая" / "Площадь"                 → Room.ProjectArea
///   "Жилая площадь" / "LivingArea"                → Room.LivingArea
///   "Общая площадь" / "TotalArea"                 → Room.TotalArea
///   "Количество комнат" / "Rooms"                 → Room.RoomsNumber
///   "Студия" / "IsStudio"                         → Room.IsStudio   (Да/Нет, true/false, 1/0)
///   "Уникальный номер" / "UniqueNumber"           → Room.UniqueNumber
///   "Стоимость" / "Cost"                          → Room.Cost
///
/// Целевой <c>SiteId</c> берётся из <see cref="ImportContext.VisarySiteId"/> (выбран в UI).
/// Если в файле есть колонка с другим SiteId — приоритет у контекста.
/// </summary>
public sealed class RoomsImportMapper : IImportMapper
{
    public string ImportTypeCode => "rooms";

    private static readonly string[] NumberAliases     = ["Номер квартиры", "Номер", "Number"];
    private static readonly string[] FloorAliases      = ["Этаж", "Floor"];
    private static readonly string[] KindAliases       = ["Тип", "Вид помещения", "Kind", "KindTitle"];
    private static readonly string[] ProjectAreaAliases= ["Площадь по проекту", "ProjectArea", "Площадь общая", "Площадь"];
    private static readonly string[] LivingAreaAliases = ["Жилая площадь", "LivingArea"];
    private static readonly string[] TotalAreaAliases  = ["Общая площадь", "TotalArea"];
    private static readonly string[] RoomsAliases      = ["Количество комнат", "Rooms", "RoomsNumber"];
    private static readonly string[] StudioAliases     = ["Студия", "IsStudio"];
    private static readonly string[] UniqueAliases     = ["Уникальный номер", "UniqueNumber"];
    private static readonly string[] CostAliases       = ["Стоимость", "Cost"];

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
                "Для импорта помещений необходимо выбрать объект строительства (Site)."));
            return new ValidationResult([], fileErrors);
        }

        // Проверяем существование выбранного site.
        var siteExists = await visaryDb.ConstructionSites
            .AsNoTracking()
            .AnyAsync(s => s.Id == context.VisarySiteId.Value && !s.Hidden, ct);
        if (!siteExists)
        {
            fileErrors.Add(new RowError(null, "site_not_found",
                $"Объект строительства с ID={context.VisarySiteId} не найден или скрыт."));
            return new ValidationResult([], fileErrors);
        }

        // Загружаем справочник RoomKind (Title → Id) — кеш на одну сессию.
        var kindByTitle = await visaryDb.RoomKinds
            .AsNoTracking()
            .Where(k => !k.Hidden)
            .ToDictionaryAsync(
                k => (k.Title ?? string.Empty).Trim(),
                k => k.Id,
                StringComparer.OrdinalIgnoreCase,
                ct);

        var mappedRows = new List<MappedRow>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var rowErrors = new List<RowError>();

            // ── Number (опц.)
            var number = ReadString(row, NumberAliases);

            // ── Floor (опц.)
            var floor = ReadString(row, FloorAliases);

            // ── KindId (обяз.)
            var kindTitle = ReadString(row, KindAliases);
            int kindId = 0;
            if (string.IsNullOrWhiteSpace(kindTitle))
            {
                rowErrors.Add(new RowError(string.Join(" / ", KindAliases), "required_missing",
                    "Не указан вид помещения (Тип/Kind)."));
            }
            else if (!kindByTitle.TryGetValue(kindTitle.Trim(), out kindId))
            {
                rowErrors.Add(new RowError(string.Join(" / ", KindAliases), "fk_not_found",
                    $"Вид помещения '{kindTitle}' не найден в справочнике RoomKind."));
            }

            // ── ProjectArea (обяз., NOT NULL в БД)
            double projectArea = 0;
            var projectAreaRaw = ReadString(row, ProjectAreaAliases);
            if (string.IsNullOrWhiteSpace(projectAreaRaw))
            {
                rowErrors.Add(new RowError(string.Join(" / ", ProjectAreaAliases), "required_missing",
                    "Не указана площадь помещения."));
            }
            else if (!TryParseDouble(projectAreaRaw, out projectArea) || projectArea < 0)
            {
                rowErrors.Add(new RowError(string.Join(" / ", ProjectAreaAliases), "invalid_number",
                    $"Площадь '{projectAreaRaw}' не является валидным числом."));
            }

            // ── Опциональные числовые
            double? totalArea = TryParseNullableDouble(ReadString(row, TotalAreaAliases), out var taErr);
            if (taErr is not null) rowErrors.Add(new RowError(string.Join(" / ", TotalAreaAliases), "invalid_number", taErr));

            double? livingArea = TryParseNullableDouble(ReadString(row, LivingAreaAliases), out var laErr);
            if (laErr is not null) rowErrors.Add(new RowError(string.Join(" / ", LivingAreaAliases), "invalid_number", laErr));

            int? roomsNumber = TryParseNullableInt(ReadString(row, RoomsAliases), out var rnErr);
            if (rnErr is not null) rowErrors.Add(new RowError(string.Join(" / ", RoomsAliases), "invalid_number", rnErr));

            decimal? cost = TryParseNullableDecimal(ReadString(row, CostAliases), out var costErr);
            if (costErr is not null) rowErrors.Add(new RowError(string.Join(" / ", CostAliases), "invalid_number", costErr));

            bool isStudio = TryParseBool(ReadString(row, StudioAliases));

            var uniqueNumber = ReadString(row, UniqueAliases);

            // ── Сериализуем mapped-значения в JSON для StagedRow.
            var mapped = new Dictionary<string, object?>
            {
                ["SiteID"]       = context.VisarySiteId,
                ["KindID"]       = kindId == 0 ? null : kindId,
                ["Number"]       = number,
                ["Floor"]        = floor,
                ["IsStudio"]     = isStudio,
                ["RoomsNumber"]  = roomsNumber,
                ["TotalArea"]    = totalArea,
                ["LivingArea"]   = livingArea,
                ["ProjectArea"]  = projectArea,
                ["UniqueNumber"] = uniqueNumber,
                ["Cost"]         = cost,
                ["Hidden"]       = false,
            };
            var doc = JsonSerializer.SerializeToDocument(mapped);

            mappedRows.Add(new MappedRow(row.SourceRowNumber, rowErrors.Count == 0, doc, rowErrors));
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
        int applied = 0;

        await using var tx = await visaryDb.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var mr in rows)
            {
                if (!mr.IsValid) continue;
                var v = mr.MappedValues.RootElement;
                var room = new Room
                {
                    SiteId       = v.GetProperty("SiteID").GetInt32(),
                    KindId       = v.GetProperty("KindID").GetInt32(),
                    Number       = GetStringOrNull(v, "Number"),
                    Floor        = GetStringOrNull(v, "Floor"),
                    IsStudio     = v.GetProperty("IsStudio").GetBoolean(),
                    RoomsNumber  = GetIntOrNull(v, "RoomsNumber"),
                    TotalArea    = GetDoubleOrNull(v, "TotalArea"),
                    LivingArea   = GetDoubleOrNull(v, "LivingArea"),
                    ProjectArea  = v.GetProperty("ProjectArea").GetDouble(),
                    UniqueNumber = GetStringOrNull(v, "UniqueNumber"),
                    Cost         = GetDecimalOrNull(v, "Cost"),
                    Hidden       = false,
                };
                visaryDb.Rooms.Add(room);
                applied++;
            }

            await visaryDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            errors.Add(new RowError(null, "apply_failed",
                $"Ошибка применения в visary_db (rollback): {ex.InnerException?.Message ?? ex.Message}"));
            applied = 0;
        }

        return new ApplyResult(applied, errors);
    }

    // ─────────────────── Helpers ───────────────────
    private static string ReadString(ParsedRow row, string[] aliases)
    {
        foreach (var key in aliases)
        {
            if (row.Cells.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        // case-insensitive fallback
        foreach (var key in aliases)
        {
            var match = row.Cells.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key) && !string.IsNullOrWhiteSpace(match.Value))
                return match.Value.Trim();
        }
        return string.Empty;
    }

    private static bool TryParseDouble(string s, out double result)
    {
        s = s.Replace(',', '.').Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static double? TryParseNullableDouble(string s, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (TryParseDouble(s, out var v)) return v;
        error = $"'{s}' не является валидным числом.";
        return null;
    }

    private static int? TryParseNullableInt(string s, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        error = $"'{s}' не является валидным целым числом.";
        return null;
    }

    private static decimal? TryParseNullableDecimal(string s, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace(',', '.').Trim();
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        error = $"'{s}' не является валидным денежным значением.";
        return null;
    }

    private static bool TryParseBool(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().ToLowerInvariant();
        return s is "true" or "1" or "да" or "yes" or "y" or "истина";
    }

    private static string? GetStringOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetIntOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? GetDoubleOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static decimal? GetDecimalOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
}
