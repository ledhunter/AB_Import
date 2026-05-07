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
/// 🏗️ Заготовка маппера импорта типа <c>roomsForm</c> — реестр помещений
/// по шаблону «Единая форма 3» (см. <c>RoomImport/Единая форма 3.xlsx</c>
/// и <c>RoomImport/room_sa_create.puml</c>).
///
/// Сценарий из puml:
///   1. xlsx с листами "Квартиры" / "Машиноместа" / "др. тип помещения";
///      "Справочник" игнорируем.
///   2. По каждой строке:
///        a) находим ConstructionSite по (РНС+Этап / РНС+НПС+Этап / НПС+Этап);
///        b) при отсутствии РНС в Site — обновляем (TODO: расширить SitePatchRequest);
///        c) находим/создаём Organization-застройщика (PIN из ячейки) и привязываем
///           участником с ролью «Застройщик» (TODO: метод link participant);
///        d) находим/создаём Section по Title (числовая часть из «Лит 1.1» → «1.1»);
///        e) находим/создаём Room по (Site, Section, Number);
///        f) находим/создаём ShareAgreement по № ДДУ.
///
/// ⚠️ Это заготовка: <see cref="ValidateAsync"/> реализован полностью, в
/// <see cref="ApplyAsync"/> вызовы Visary API расставлены по сценарию, но места,
/// требующие либо расширения клиента, либо уточнения бизнес-правил, помечены TODO.
/// Существующий <see cref="RoomsImportMapper"/> (код "rooms") НЕ изменяется.
/// </summary>
public sealed class RoomsFormImportMapper : IImportMapper
{
    public string ImportTypeCode => "roomsForm";

    private static readonly HashSet<string> SkippedSheets =
        new(StringComparer.OrdinalIgnoreCase) { "Справочник" };

    // === Алиасы колонок (case-insensitive) =================================
    // Заголовки взяты из RoomImport/Единая форма 3.xlsx, row 2 (человеко-читаемые)
    // и row 3 (технический mapping "Data"."Entity"."Field").
    private static readonly string[] DeveloperPinAliases     = ["ПИН застройщика", "DeveloperPIN"];
    private static readonly string[] PermissionNumberAliases = ["Номер разрешения", "ConstructionPermissionNumber", "РНС"];
    private static readonly string[] ProjectNumberAliases    = ["Номер проекта", "ConstructionProjectNumber", "НПС"];
    private static readonly string[] StageNumberAliases      = ["Этап", "StageNumber"];
    private static readonly string[] RoomNumberAliases       = [
        "Номер помещения/Квартира/Номер квартиры",
        "Номер помещения", "Номер квартиры", "Квартира", "ExplicationNumber"];
    private static readonly string[] RoomKindAliases         = ["Тип/Название/Вид", "Тип", "Вид", "Kind"];
    private static readonly string[] SectionTitleAliases     = ["№ стр/корп", "Section", "Строение"];
    private static readonly string[] FloorAliases            = ["Этаж", "Floor"];
    private static readonly string[] BuildingSectionAliases  = ["Подъезд/Секция", "BuildingSection"];
    private static readonly string[] RoomsCountAliases       = ["Колич. комнат", "Количество комнат", "RoomsNumber"];
    private static readonly string[] ProjectAreaAliases      = [
        "Площадь (для квартир с балконами и лоджиями с Кб=0,3; Кл=0,5), кв.м.",
        "Площадь", "ProjectArea"];
    private static readonly string[] CostForOneAliases       = ["Стоимость кв,м/ руб,", "Стоимость кв.м", "CostForOne"];
    private static readonly string[] WholesaleRateAliases    = ["Скидка на опт.", "WholesaleRate"];
    private static readonly string[] MarketCostAliases       = ["Рыночная стоимость, руб.", "MarketCostPerM"];
    private static readonly string[] ZalogCostAliases        = ["Залоговая стоимость.", "ZalogCostPerM"];
    private static readonly string[] ShareAgreementAliases   = ["№ ДДУ", "ShareAgreementNumber"];

    private readonly ILogger<RoomsFormImportMapper> _log;
    private readonly IListViewClient _listView;
    private readonly ICrudClient     _crud;

    public RoomsFormImportMapper(
        ILogger<RoomsFormImportMapper> log,
        IListViewClient listView,
        ICrudClient crud)
    {
        _log = log;
        _listView = listView;
        _crud = crud;
    }

    // ──────────────────────────────── Validate ──────────────────────────────
    public async Task<ValidationResult> ValidateAsync(
        ImportContext context,
        IReadOnlyList<ParsedRow> rows,
        VisaryDbContext visaryDb,
        CancellationToken ct)
    {
        var fileErrors = new List<RowError>();

        // Кэш RoomKind: Title → ID. Только для валидации, без обращения в API.
        var kindByTitle = await visaryDb.RoomKinds
            .AsNoTracking()
            .Where(k => !k.Hidden)
            .ToDictionaryAsync(
                k => (k.Title ?? string.Empty).Trim(),
                k => k.Id,
                StringComparer.OrdinalIgnoreCase, ct);

        var dataRows = rows.Where(r => !SkippedSheets.Contains(r.Sheet)).ToList();
        if (dataRows.Count == 0)
        {
            fileErrors.Add(new RowError(null, "no_data",
                "В файле нет строк с данными (только служебный лист «Справочник» или пустые листы)."));
            return new ValidationResult([], fileErrors);
        }

        var mappedRows = new List<MappedRow>(dataRows.Count);
        foreach (var row in dataRows)
        {
            ct.ThrowIfCancellationRequested();
            var rowErrors = new List<RowError>();

            // ── Ключи поиска Site ───────────────────────────────────────────
            var permission  = ReadString(row, PermissionNumberAliases);
            var projectNum  = ReadString(row, ProjectNumberAliases);
            var stageNumRaw = ReadString(row, StageNumberAliases);
            if (string.IsNullOrWhiteSpace(permission) &&
                string.IsNullOrWhiteSpace(projectNum) &&
                string.IsNullOrWhiteSpace(stageNumRaw))
            {
                rowErrors.Add(new RowError(null, "site_keys_missing",
                    "Не указаны ключи поиска объекта строительства (РНС / НПС / Этап)."));
            }

            // ── Поля поиска Room ────────────────────────────────────────────
            var roomNumber = ReadString(row, RoomNumberAliases);
            if (string.IsNullOrWhiteSpace(roomNumber))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomNumberAliases), "required_missing",
                    "Не указан номер помещения."));
            }

            var roomKindTitle = ReadString(row, RoomKindAliases);
            int kindId = 0;
            if (string.IsNullOrWhiteSpace(roomKindTitle))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomKindAliases), "required_missing",
                    "Не указан вид помещения."));
            }
            else if (!kindByTitle.TryGetValue(roomKindTitle.Trim(), out kindId))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomKindAliases), "fk_not_found",
                    $"Вид помещения '{roomKindTitle}' не найден в справочнике RoomKind."));
            }

            // ── Прочие поля ─────────────────────────────────────────────────
            var sectionTitle    = ReadString(row, SectionTitleAliases);
            var floor           = ReadString(row, FloorAliases);
            var buildingSection = ReadString(row, BuildingSectionAliases);
            var developerPin    = ReadString(row, DeveloperPinAliases);
            var shareAgreement  = ReadString(row, ShareAgreementAliases);

            int? roomsCount = TryParseNullableInt(ReadString(row, RoomsCountAliases), out var rcErr);
            if (rcErr != null) rowErrors.Add(new RowError(string.Join(" / ", RoomsCountAliases), "invalid_number", rcErr));

            double? projectArea = TryParseNullableDouble(ReadString(row, ProjectAreaAliases), out var paErr);
            if (paErr != null) rowErrors.Add(new RowError(string.Join(" / ", ProjectAreaAliases), "invalid_number", paErr));

            double? costForOne = TryParseNullableDouble(ReadString(row, CostForOneAliases), out var cErr);
            if (cErr != null) rowErrors.Add(new RowError(string.Join(" / ", CostForOneAliases), "invalid_number", cErr));

            double? wholesale  = TryParseNullableDouble(ReadString(row, WholesaleRateAliases), out var wErr);
            if (wErr != null) rowErrors.Add(new RowError(string.Join(" / ", WholesaleRateAliases), "invalid_number", wErr));

            double? marketCost = TryParseNullableDouble(ReadString(row, MarketCostAliases), out var mErr);
            if (mErr != null) rowErrors.Add(new RowError(string.Join(" / ", MarketCostAliases), "invalid_number", mErr));

            double? zalogCost  = TryParseNullableDouble(ReadString(row, ZalogCostAliases), out var zErr);
            if (zErr != null) rowErrors.Add(new RowError(string.Join(" / ", ZalogCostAliases), "invalid_number", zErr));

            int? stageInt = null;
            if (!string.IsNullOrWhiteSpace(stageNumRaw)
                && int.TryParse(stageNumRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                stageInt = s;

            var mapped = new Dictionary<string, object?>
            {
                ["Sheet"]                = row.Sheet,
                ["DeveloperPin"]         = developerPin,
                ["PermissionNumber"]     = permission,
                ["ProjectNumber"]        = projectNum,
                ["StageNumber"]          = stageInt,
                ["StageNumberRaw"]       = stageNumRaw,
                ["RoomNumber"]           = roomNumber,
                ["RoomKindTitle"]        = roomKindTitle,
                ["RoomKindId"]           = kindId == 0 ? null : (int?)kindId,
                ["SectionTitle"]         = sectionTitle,
                ["SectionTitleNumeric"] = ExtractNumericPart(sectionTitle),
                ["Floor"]                = floor,
                ["BuildingSection"]      = buildingSection,
                ["RoomsCount"]           = roomsCount,
                ["ProjectArea"]          = projectArea,
                ["CostForOne"]           = costForOne,
                ["WholesaleRate"]        = wholesale,
                ["MarketCostPerM"]       = marketCost,
                ["ZalogCostPerM"]        = zalogCost,
                ["ShareAgreementNumber"] = shareAgreement,
            };
            mappedRows.Add(new MappedRow(
                row.SourceRowNumber,
                rowErrors.Count == 0,
                JsonSerializer.SerializeToDocument(mapped),
                rowErrors));
        }

        _log.LogInformation(
            "RoomsFormImportMapper.Validate: rows={Total}, valid={Valid}, fileErrors={FileErrors}",
            mappedRows.Count, mappedRows.Count(r => r.IsValid), fileErrors.Count);
        return new ValidationResult(mappedRows, fileErrors);
    }

    // ──────────────────────────────── Apply ─────────────────────────────────
    public async Task<ApplyResult> ApplyAsync(
        ImportContext context,
        VisaryDbContext visaryDb,
        IReadOnlyList<MappedRow> rows,
        CancellationToken ct)
    {
        var errors  = new List<RowError>();
        var applied = 0;

        // Кэши, чтобы не дёргать Visary API на каждой строке заново.
        var siteCache    = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var sectionCache = new Dictionary<(int siteId, string title), int>();
        var orgCache     = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        // Per-Site защита от повторных проверок «РНС пуст?» и «застройщик уже привязан?».
        var sitePermissionPatched = new HashSet<int>();
        var siteOrgLinked         = new HashSet<(int siteId, int orgId)>();

        foreach (var mr in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!mr.IsValid) continue;
            var v = mr.MappedValues.RootElement;

            try
            {
                // ── 1. ConstructionSite ─────────────────────────────────────
                var permission  = GetStringOrNull(v, "PermissionNumber");
                var projectNum  = GetStringOrNull(v, "ProjectNumber");
                var stageNumRaw = GetStringOrNull(v, "StageNumberRaw");
                var siteKey     = $"{permission}|{projectNum}|{stageNumRaw}";

                if (!siteCache.TryGetValue(siteKey, out var siteId))
                {
                    siteId = await FindSiteAsync(permission, projectNum, stageNumRaw, ct);
                    siteCache[siteKey] = siteId;
                }
                if (siteId is null)
                {
                    errors.Add(new RowError(null, "site_not_found",
                        $"row {mr.SourceRowNumber}: ConstructionSite не найден по " +
                        $"РНС='{permission}' / НПС='{projectNum}' / Этап='{stageNumRaw}'."));
                    continue;
                }

                // ── 2. Обновление РНС в Site, если в Site не указан ─────────
                // Один раз на Site за сессию: GetSiteByIdFullAsync + (при необходимости) PATCH.
                if (!string.IsNullOrWhiteSpace(permission) && !sitePermissionPatched.Contains(siteId.Value))
                {
                    var fullSite = await _crud.GetSiteByIdFullAsync(siteId.Value, ct);
                    if (string.IsNullOrWhiteSpace(fullSite.ConstructionPermissionNumber))
                    {
                        await _crud.PatchSiteAsync(siteId.Value, new SitePatchRequest
                        {
                            RowVersion                   = fullSite.RowVersion,
                            ConstructionPermissionNumber = permission,
                        }, ct);
                        _log.LogInformation(
                            "RoomsForm.Apply: siteId={SiteId} → updated ConstructionPermissionNumber='{Perm}'",
                            siteId.Value, permission);
                    }
                    sitePermissionPatched.Add(siteId.Value);
                }

                // ── 3. Organization-застройщик: найти и привязать к Site ────
                var devPin = GetStringOrNull(v, "DeveloperPin");
                if (!string.IsNullOrWhiteSpace(devPin))
                {
                    if (!orgCache.TryGetValue(devPin, out var orgId))
                    {
                        var orgs = await _listView.GetOrganizationsByClientIdAsync(devPin, ct);
                        orgId = orgs.Data.FirstOrDefault()?.ID;
                        orgCache[devPin] = orgId;
                    }
                    // По puml: если Organization не найдена — переходим к Section
                    // (без падения), поэтому проверяем условно.
                    if (orgId is not null
                        && siteOrgLinked.Add((siteId.Value, orgId.Value)))
                    {
                        try
                        {
                            await _crud.LinkOrganizationToSiteAsync(siteId.Value, orgId.Value, ct);
                        }
                        catch (Exception linkEx)
                        {
                            // Если связь уже существует, Visary возвращает ошибку — это не блокер.
                            _log.LogWarning(linkEx,
                                "RoomsForm.Apply: link Organization {OrgId} → Site {SiteId} skipped: {Msg}",
                                orgId.Value, siteId.Value, linkEx.Message);
                        }
                    }
                }

                // ── 4. Section: найти/создать ───────────────────────────────
                var sectionTitle = GetStringOrNull(v, "SectionTitleNumeric")
                                   ?? GetStringOrNull(v, "SectionTitle");
                int? sectionId = null;
                if (!string.IsNullOrWhiteSpace(sectionTitle))
                {
                    var key = (siteId.Value, sectionTitle);
                    if (!sectionCache.TryGetValue(key, out var cached))
                    {
                        var existing = await _listView.GetSectionsBySiteAsync(siteId.Value, sectionTitle, ct);
                        var match = existing.Data.FirstOrDefault(x =>
                            string.Equals(x.Title, sectionTitle, StringComparison.OrdinalIgnoreCase));
                        if (match is not null)
                        {
                            cached = match.ID;
                        }
                        else
                        {
                            // TODO: уточнить значения Section.Type / BuildingMaterial / Stage —
                            //       в файле «Единая форма 3» этих полей нет (см. puml: «откуда
                            //       будем знать, какой тип у секции?»).
                            var created = await _crud.CreateSectionAsync(new SectionCreateRequest
                            {
                                ConstructionSiteID = siteId.Value,
                                ConstructionSite   = new VisaryRef { ID = siteId.Value },
                                Title              = sectionTitle,
                            }, ct);
                            cached = created.ID;
                        }
                        sectionCache[key] = cached;
                    }
                    sectionId = cached;
                }

                // ── 5. Room: найти/создать ──────────────────────────────────
                var roomNumber = GetStringOrNull(v, "RoomNumber") ?? string.Empty;
                int? roomId = null;
                if (sectionId is not null)
                {
                    var roomsInSection = await _listView.GetRoomsBySectionAsync(sectionId.Value, null, ct);
                    var match = roomsInSection.Data.FirstOrDefault(r =>
                        string.Equals(r.ExplicationNumber, roomNumber, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.Number,            roomNumber, StringComparison.OrdinalIgnoreCase));
                    roomId = match?.ID;
                }

                var kindId = GetIntOrNull(v, "RoomKindId");
                if (roomId is null)
                {
                    var created = await _crud.CreateRoomAsync(new RoomCreateRequest
                    {
                        SiteID            = siteId.Value,
                        Site              = new VisaryRef { ID = siteId.Value },
                        Title             = roomNumber,
                        ExplicationNumber = roomNumber,
                        Section           = sectionId is null ? null : new VisaryRef { ID = sectionId.Value },
                        Kind              = kindId    is null ? null : new VisaryRef { ID = kindId.Value },
                        Floor             = GetStringOrNull(v, "Floor"),
                        BuildingSection   = GetStringOrNull(v, "BuildingSection"),
                        RoomsNumber       = GetIntOrNull(v, "RoomsCount"),
                        ProjectArea       = GetDoubleOrNull(v, "ProjectArea"),
                        CostForOne        = GetDoubleOrNull(v, "CostForOne"),
                        MarketCostPerM    = GetDoubleOrNull(v, "MarketCostPerM"),
                        ZalogCostPerM     = GetDoubleOrNull(v, "ZalogCostPerM"),
                    }, ct);
                    roomId = created.ID;
                }
                else
                {
                    // PATCH с forceUpdate=true: обновляем измеримые поля из файла.
                    // Title/ExplicationNumber не трогаем — могут расходиться с
                    // тем, что уже было ручно задано в Visary.
                    await _crud.PatchRoomAsync(roomId.Value, new RoomPatchRequest
                    {
                        Section         = sectionId is null ? null : new VisaryRef { ID = sectionId.Value },
                        Kind            = kindId    is null ? null : new VisaryRef { ID = kindId.Value },
                        Floor           = GetStringOrNull(v, "Floor"),
                        BuildingSection = GetStringOrNull(v, "BuildingSection"),
                        RoomsNumber     = GetIntOrNull(v, "RoomsCount"),
                        ProjectArea     = GetDoubleOrNull(v, "ProjectArea"),
                        CostForOne      = GetDoubleOrNull(v, "CostForOne"),
                        MarketCostPerM  = GetDoubleOrNull(v, "MarketCostPerM"),
                        ZalogCostPerM   = GetDoubleOrNull(v, "ZalogCostPerM"),
                    }, ct);
                }

                // ── 6. ShareAgreement: найти/создать/обновить ───────────────
                var saNumber = GetStringOrNull(v, "ShareAgreementNumber");
                if (!string.IsNullOrWhiteSpace(saNumber) && roomId is not null)
                {
                    var sas = await _listView.GetShareAgreementsByRoomAsync(roomId.Value, saNumber, ct);
                    var saMatch = sas.Data.FirstOrDefault(a =>
                        string.Equals(a.Number, saNumber, StringComparison.OrdinalIgnoreCase));
                    if (saMatch is null)
                    {
                        await _crud.CreateShareAgreementAsync(new ShareAgreementCreateRequest
                        {
                            RoomID = roomId.Value,
                            Room   = new VisaryRef { ID = roomId.Value },
                            Site   = new VisaryRef { ID = siteId.Value },
                            Number = saNumber,
                            Title  = saNumber,
                        }, ct);
                    }
                    else
                    {
                        // Найден — мягко обновляем привязки к Site (на случай, если в Visary
                        // этот ДДУ создавался без указания Site).
                        await _crud.PatchShareAgreementAsync(saMatch.ID, new ShareAgreementPatchRequest
                        {
                            Number = saNumber,
                            Site   = new VisaryRef { ID = siteId.Value },
                        }, ct);
                    }
                }

                applied++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "RoomsFormImportMapper.Apply row {RowNum} failed: {Msg}",
                    mr.SourceRowNumber, ex.Message);
                errors.Add(new RowError(null, "apply_failed",
                    $"row {mr.SourceRowNumber}: {ex.Message}"));
            }
        }

        return new ApplyResult(applied, errors);
    }

    // ──────────────────────────── Поиск Site ────────────────────────────────
    /// <summary>
    /// Три стратегии из puml (room_sa_create.puml шаг 32):
    ///   1) РНС + Этап
    ///   2) РНС + НПС + Этап
    ///   3) НПС + Этап
    /// Возвращает ID после первой непустой выборки. Если все стратегии вернули пусто — null.
    /// </summary>
    private async Task<int?> FindSiteAsync(
        string? permission, string? projectNum, string? stageNumRaw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(permission)
            && string.IsNullOrWhiteSpace(projectNum)
            && string.IsNullOrWhiteSpace(stageNumRaw))
            return null;

        // 1) РНС + Этап
        if (!string.IsNullOrWhiteSpace(permission) && !string.IsNullOrWhiteSpace(stageNumRaw))
        {
            var r1 = await _listView.FindSitesAsync(permission, null, stageNumRaw, ct);
            if (r1.Data.Count > 0) return r1.Data[0].ID;
        }

        // 2) РНС + НПС + Этап
        if (!string.IsNullOrWhiteSpace(permission)
            && !string.IsNullOrWhiteSpace(projectNum)
            && !string.IsNullOrWhiteSpace(stageNumRaw))
        {
            var r2 = await _listView.FindSitesAsync(permission, projectNum, stageNumRaw, ct);
            if (r2.Data.Count > 0) return r2.Data[0].ID;
        }

        // 3) НПС + Этап
        if (!string.IsNullOrWhiteSpace(projectNum) && !string.IsNullOrWhiteSpace(stageNumRaw))
        {
            var r3 = await _listView.FindSitesAsync(null, projectNum, stageNumRaw, ct);
            if (r3.Data.Count > 0) return r3.Data[0].ID;
        }

        _log.LogWarning(
            "RoomsForm.FindSiteAsync: ConstructionSite не найден по РНС={Perm} НПС={Proj} Этап={Stage}",
            permission, projectNum, stageNumRaw);
        return null;
    }

    // ──────────────────────────── Helpers ──────────────────────────────────
    /// <summary>«Лит 1.1» → «1.1»; «корп 2» → «2»; «3.А» → «3».</summary>
    private static string? ExtractNumericPart(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsDigit(ch) || ch == '.' || ch == ',')
                sb.Append(ch == ',' ? '.' : ch);
            else if (sb.Length > 0 && ch != ' ')
                break;
        }
        return sb.Length == 0 ? null : sb.ToString().Trim('.');
    }

    private static string ReadString(ParsedRow row, string[] aliases)
    {
        foreach (var key in aliases)
        {
            if (row.Cells.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        foreach (var key in aliases)
        {
            var match = row.Cells.FirstOrDefault(p =>
                string.Equals(p.Key.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key) && !string.IsNullOrWhiteSpace(match.Value))
                return match.Value.Trim();
        }
        return string.Empty;
    }

    private static bool TryParseDouble(string s, out double result)
    {
        s = s.Replace(',', '.').Replace(" ", string.Empty).Trim();
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
        if (int.TryParse(s.Replace(" ", string.Empty).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return v;
        error = $"'{s}' не является валидным целым числом.";
        return null;
    }

    private static string? GetStringOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetIntOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? GetDoubleOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
