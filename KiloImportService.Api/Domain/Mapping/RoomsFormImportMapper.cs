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
/// 🏗️ Маппер импорта типа <c>rooms</c> — реестр помещений по файлу
/// «Пример импорта.xlsx» / «Единая форма 3» (см. <c>RoomImport/</c>).
///
/// Контракт: пользователь УЖЕ выбирает <c>Project</c> и <c>Site</c> (ОКС) в UI;
/// маппер только валидирует, что строки файла соответствуют выбранному ОКСу,
/// а затем для каждой подходящей строки находит/создаёт корпус и помещение.
///
/// Per-row проверки (если не пройдены — строка пропускается с лог-сообщением,
/// «для строки файла {N} не подходит выбранный объект»):
///   1. ConstructionProjectNumber (Site) == «Номер проекта» (файл)
///   2. StageNumber (Site)              == «Этап»          (файл)
///   3. (опц.) ConstructionPermissionNumber (Site) == «Номер разрешения» (файл),
///      если значение в файле непустое.
///
/// Если строка прошла проверку — переходим к поиску корпуса в выбранном Site:
///   • Из «№ стр/корп» извлекаем числовую часть («лит 1.1» → «1.1»).
///   • Ищем Section через listview, при отсутствии — создаём
///     (лог «для строки файла {N} нет подходящего корпуса, поэтому он будет создан»).
///
/// Дальнейшая логика (Room/ShareAgreement) сохранена из предыдущей версии маппера
/// «roomsForm» — она писалась под тот же файл и переиспользуется без изменений
/// после уточнения сценария.
/// </summary>
public sealed class RoomsFormImportMapper : IImportMapper
{
    public string ImportTypeCode => "rooms";

    private static readonly HashSet<string> SkippedSheets =
        new(StringComparer.OrdinalIgnoreCase) { "Справочник" };

    // === Алиасы колонок (case-insensitive) =================================
    // Заголовки взяты из RoomImport/Пример импорта.xlsx (row 1) и
    // RoomImport/Единая форма 3.xlsx (row 2 — человекочитаемые / row 3 — техн.).
    private static readonly string[] DeveloperPinAliases     = ["ПИН застройщика", "DeveloperPIN"];
    private static readonly string[] PermissionNumberAliases = ["Номер разрешения", "ConstructionPermissionNumber", "РНС"];
    private static readonly string[] ProjectNumberAliases    = ["Номер проекта", "ConstructionProjectNumber", "НПС"];
    private static readonly string[] StageNumberAliases      = ["Этап", "Номер этапа", "StageNumber"];
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

        // Site обязателен — пользователь выбирает ОКС в UI.
        if (context.VisarySiteId is null)
        {
            fileErrors.Add(new RowError(null, "site_required",
                "Для импорта помещений необходимо выбрать объект строительства (Site)."));
            return new ValidationResult([], fileErrors);
        }

        // Загружаем выбранный ОКС, чтобы сверять с ним строки файла.
        ConstructionSiteFull site;
        try
        {
            site = await _crud.GetSiteByIdFullAsync(context.VisarySiteId.Value, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RoomsForm.Validate: не удалось загрузить Site {SiteId}",
                context.VisarySiteId.Value);
            fileErrors.Add(new RowError(null, "site_fetch_failed",
                $"Не удалось получить ОКС {context.VisarySiteId.Value} из Visary: {ex.Message}"));
            return new ValidationResult([], fileErrors);
        }

        var siteProjectNumber    = (site.ConstructionProjectNumber ?? string.Empty).Trim();
        var sitePermissionNumber = (site.ConstructionPermissionNumber ?? string.Empty).Trim();
        var siteStageNumber      = site.StageNumber;
        _log.LogInformation(
            "RoomsForm.Validate: siteId={SiteId} projectNum='{P}' stage={S} permission='{Perm}'",
            site.ID, siteProjectNumber, siteStageNumber, sitePermissionNumber);

        // Кэш RoomKind: Title → ID. Берём из живого Visary API (не из локальной visary_db),
        // т.к. seed-данные локальной БД могут не совпадать с реальным справочником на стенде —
        // это приводило к тому, что «Машиноместо» / «Квартира» не резолвились,
        // строки помечались invalid, и импортировался только один лист с ближайшим
        // совпадением (например, «Гараж» по substring).
        var roomKindList = await _listView.ListRoomKindsAsync(ct);
        var kindByTitle = roomKindList.Data
            .Where(k => !string.IsNullOrWhiteSpace(k.Title))
            .GroupBy(k => k.Title!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ID, StringComparer.OrdinalIgnoreCase);
        _log.LogInformation(
            "RoomsForm.Validate: загружен справочник RoomKind из Visary — {Count} записей: {Titles}",
            kindByTitle.Count, string.Join(", ", kindByTitle.Select(kv => $"{kv.Key}={kv.Value}")));

        var dataRows = rows.Where(r => !SkippedSheets.Contains(r.Sheet)).ToList();
        if (dataRows.Count == 0)
        {
            fileErrors.Add(new RowError(null, "no_data",
                "В файле нет строк с данными (только служебный лист «Справочник» или пустые листы)."));
            return new ValidationResult([], fileErrors);
        }

        // Один лист = один тип помещений. Для каждого листа резолвим RoomKind по
        // имени листа: «Квартиры» → ищем «Квартира» в справочнике, «Машиноместа» →
        // «Машиноместо». Используется как fallback, когда строка не указывает «Тип/Название/Вид»,
        // и для warn'ов когда тип строки расходится с типом листа.
        var sheetKindCache = new Dictionary<string, (int? Id, string? Title)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheetName in dataRows.Select(r => r.Sheet).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (sId, sTitle) = ResolveKindBySheetName(sheetName, kindByTitle);
            sheetKindCache[sheetName] = (sId, sTitle);
            _log.LogInformation(
                "RoomsForm.Validate: лист '{Sheet}' → ожидаемый вид помещений '{Title}' (ID={Id})",
                sheetName, sTitle ?? "<не определён>", sId?.ToString() ?? "—");
        }

        var mappedRows = new List<MappedRow>(dataRows.Count);
        foreach (var row in dataRows)
        {
            ct.ThrowIfCancellationRequested();
            var rowErrors = new List<RowError>();

            // ── Ключи Site из строки ───────────────────────────────────────
            var permission  = ReadString(row, PermissionNumberAliases);
            var projectNum  = ReadString(row, ProjectNumberAliases);
            var stageNumRaw = ReadString(row, StageNumberAliases);

            // ── Per-row сверка Site (НЕ ИЩЕМ Site, а ВАЛИДИРУЕМ выбранный) ─
            // Жёсткие проверки: НПС и Этап должны совпадать. Опционально РНС
            // (если пустой в файле — пропускаем эту проверку, как описано в задаче).
            var rowProjectNum = projectNum.Trim();
            int? rowStageNum  = ParseNullableInt(stageNumRaw);
            var rowPermission = permission.Trim();

            bool projectOk = string.Equals(rowProjectNum, siteProjectNumber, StringComparison.OrdinalIgnoreCase);
            bool stageOk   = rowStageNum.HasValue && siteStageNumber.HasValue
                          && rowStageNum.Value == siteStageNumber.Value;
            bool permissionOk = string.IsNullOrWhiteSpace(rowPermission)
                          || string.Equals(rowPermission, sitePermissionNumber, StringComparison.OrdinalIgnoreCase);

            if (!projectOk || !stageOk || !permissionOk)
            {
                _log.LogInformation(
                    "RoomsForm.Validate: row {Row} — site_mismatch: " +
                    "файл(НПС='{RP}', Этап='{RS}', РНС='{RPerm}') vs site(НПС='{SP}', Этап='{SS}', РНС='{SPerm}')",
                    row.SourceRowNumber, rowProjectNum, stageNumRaw, rowPermission,
                    siteProjectNumber, siteStageNumber, sitePermissionNumber);

                rowErrors.Add(new RowError(null, "site_mismatch",
                    $"для строки файла {row.SourceRowNumber} не подходит выбранный объект"));

                mappedRows.Add(new MappedRow(
                    row.SourceRowNumber,
                    IsValid: false,
                    JsonSerializer.SerializeToDocument(new { Sheet = row.Sheet }),
                    rowErrors));
                continue; // переходим к следующей строке без дальнейшей валидации
            }

            // ── Поля поиска Room ────────────────────────────────────────────
            var roomNumber = ReadString(row, RoomNumberAliases);
            if (string.IsNullOrWhiteSpace(roomNumber))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomNumberAliases), "required_missing",
                    "Не указан номер помещения."));
            }

            // ── Вид помещения: row.Cells["Тип/Название/Вид"] (приоритет) или sheet name (fallback)
            var roomKindTitle = ReadString(row, RoomKindAliases);
            int kindId = 0;
            var (sheetKindId, sheetKindTitle) = sheetKindCache.TryGetValue(row.Sheet, out var sk)
                ? sk
                : ((int?)null, (string?)null);

            if (string.IsNullOrWhiteSpace(roomKindTitle))
            {
                // Колонка пуста — используем тип листа.
                if (sheetKindId.HasValue)
                {
                    kindId = sheetKindId.Value;
                    roomKindTitle = sheetKindTitle!;
                    _log.LogDebug(
                        "RoomsForm.Validate: row {Row} — вид помещения определён по имени листа '{Sheet}' → '{Title}'",
                        row.SourceRowNumber, row.Sheet, sheetKindTitle);
                }
                else
                {
                    rowErrors.Add(new RowError(string.Join(" / ", RoomKindAliases), "required_missing",
                        $"Не указан вид помещения и не удалось определить его по имени листа '{row.Sheet}'."));
                }
            }
            else if (!kindByTitle.TryGetValue(roomKindTitle.Trim(), out kindId))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomKindAliases), "fk_not_found",
                    $"Вид помещения '{roomKindTitle}' не найден в справочнике RoomKind."));
            }
            else if (sheetKindId.HasValue && kindId != sheetKindId.Value)
            {
                // Колонка указывает другой вид, чем лист. Доверяем колонке, но фиксируем расхождение.
                _log.LogWarning(
                    "RoomsForm.Validate: row {Row} — вид помещения '{RowKind}' (ID={RowId}) не совпадает с типом листа '{Sheet}' → '{SheetKind}' (ID={SheetId}). Используется значение из колонки.",
                    row.SourceRowNumber, roomKindTitle, kindId, row.Sheet, sheetKindTitle, sheetKindId.Value);
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

            var mapped = new Dictionary<string, object?>
            {
                ["Sheet"]                = row.Sheet,
                ["DeveloperPin"]         = developerPin,
                ["PermissionNumber"]     = rowPermission,
                ["ProjectNumber"]        = rowProjectNum,
                ["StageNumber"]          = rowStageNum,
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
            "RoomsForm.Validate: rows={Total}, valid={Valid}, fileErrors={FileErrors}",
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

        if (context.VisarySiteId is null)
        {
            errors.Add(new RowError(null, "site_required",
                "Не указан объект строительства (visarySiteId)."));
            return new ApplyResult(0, errors);
        }

        var siteId = context.VisarySiteId.Value;

        // Кэши, чтобы не дёргать Visary API на каждой строке заново.
        var sectionCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var orgCache     = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var siteOrgLinked = new HashSet<int>();

        // ⚙️ Группируем строки по листу (один лист = один тип помещений).
        // GroupBy в LINQ сохраняет порядок появления групп = порядок листов в файле.
        var rowsBySheet = rows
            .Where(mr => mr.IsValid)
            .GroupBy(mr => GetStringOrNull(mr.MappedValues.RootElement, "Sheet") ?? "<unknown>",
                     StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sheetGroup in rowsBySheet)
        {
            ct.ThrowIfCancellationRequested();
            _log.LogInformation(
                "RoomsForm.Apply: ───── Лист '{Sheet}' — {Count} валидных строк ─────",
                sheetGroup.Key, sheetGroup.Count());

        foreach (var mr in sheetGroup)
        {
            ct.ThrowIfCancellationRequested();
            var v = mr.MappedValues.RootElement;

            try
            {
                // ── 1. Organization-застройщик: найти и привязать к Site ────
                var devPin = GetStringOrNull(v, "DeveloperPin");
                if (!string.IsNullOrWhiteSpace(devPin))
                {
                    if (!orgCache.TryGetValue(devPin, out var orgId))
                    {
                        var orgs = await _listView.GetOrganizationsByClientIdAsync(devPin, ct);
                        orgId = orgs.Data.FirstOrDefault()?.ID;
                        orgCache[devPin] = orgId;
                    }
                    if (orgId is not null && siteOrgLinked.Add(orgId.Value))
                    {
                        try
                        {
                            await _crud.LinkOrganizationToSiteAsync(siteId, orgId.Value, ct);
                        }
                        catch (Exception linkEx)
                        {
                            // Если связь уже существует, Visary возвращает ошибку — это не блокер.
                            _log.LogWarning(linkEx,
                                "RoomsForm.Apply: link Organization {OrgId} → Site {SiteId} skipped: {Msg}",
                                orgId.Value, siteId, linkEx.Message);
                        }
                    }
                }

                // ── 2. Section: найти/создать ───────────────────────────────
                var sectionTitle = GetStringOrNull(v, "SectionTitleNumeric")
                                   ?? GetStringOrNull(v, "SectionTitle");
                int? sectionId = null;
                if (!string.IsNullOrWhiteSpace(sectionTitle))
                {
                    if (!sectionCache.TryGetValue(sectionTitle, out var cached))
                    {
                        var existing = await _listView.GetSectionsBySiteAsync(siteId, sectionTitle, ct);
                        var match = existing.Data.FirstOrDefault(x =>
                            string.Equals(x.Title, sectionTitle, StringComparison.OrdinalIgnoreCase));
                        if (match is not null)
                        {
                            cached = match.ID;
                        }
                        else
                        {
                            // Логируем формулировку, заданную в задаче.
                            _log.LogInformation(
                                "RoomsForm.Apply: для строки файла {Row} нет подходящего корпуса, поэтому он будет создан (siteId={SiteId}, title='{Title}')",
                                mr.SourceRowNumber, siteId, sectionTitle);

                            // Section.Type обязателен для Visary (без него — 422).
                            // По уточнению: пока используем МЖД (ID=3) для всех корпусов;
                            // ветка «Паркинг» будет добавлена позже (TODO: динамический
                            // справочник constructionsectiontype).
                            var created = await _crud.CreateSectionAsync(new SectionCreateRequest
                            {
                                ConstructionSiteID = siteId,
                                ConstructionSite   = new VisaryRef { ID = siteId },
                                Title              = sectionTitle,
                                Type               = new VisaryRef { ID = 3, Title = "МЖД" },
                            }, ct);
                            cached = created.ID;
                        }
                        sectionCache[sectionTitle] = cached;
                    }
                    sectionId = cached;
                }

                // ── 3. Room: найти/создать ──────────────────────────────────
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
                        SiteID            = siteId,
                        Site              = new VisaryRef { ID = siteId },
                        Title             = roomNumber,
                        ExplicationNumber = roomNumber,
                        UniqueNumber      = roomNumber, // та же колонка «Номер помещения»
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

                // ── 4. ShareAgreement: найти/создать/обновить ───────────────
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
                            RoomID            = roomId.Value,
                            Room              = new VisaryRef { ID = roomId.Value },
                            Project           = context.VisaryProjectId is null
                                                    ? null
                                                    : new VisaryRef { ID = context.VisaryProjectId.Value },
                            Site              = new VisaryRef { ID = siteId },
                            RoomKindRef       = kindId is null ? null : new VisaryRef { ID = kindId.Value },
                            Number            = saNumber,
                            Title             = saNumber,
                            ProjectNumber     = GetStringOrNull(v, "ProjectNumber"),
                            ConditionalNumber = roomNumber,
                        }, ct);
                    }
                    else
                    {
                        await _crud.PatchShareAgreementAsync(saMatch.ID, new ShareAgreementPatchRequest
                        {
                            Number = saNumber,
                            Site   = new VisaryRef { ID = siteId },
                        }, ct);
                    }
                }

                applied++;
            }
            catch (Exception ex)
            {
                // Сохраняем максимум контекста: какая строка, что за шаг, входные значения,
                // и inner exception (если HttpRequestException обернул другую).
                var ctx = mr.MappedValues.RootElement;
                _log.LogError(ex,
                    "RoomsFormImportMapper.Apply row {RowNum} failed: {Msg}. " +
                    "Context: siteId={SiteId}, sectionTitle='{Section}', roomNumber='{Room}', " +
                    "kindId={KindId}, saNumber='{Sa}'. Inner: {Inner}",
                    mr.SourceRowNumber, ex.Message, siteId,
                    GetStringOrNull(ctx, "SectionTitleNumeric") ?? GetStringOrNull(ctx, "SectionTitle"),
                    GetStringOrNull(ctx, "RoomNumber"),
                    GetIntOrNull(ctx, "RoomKindId"),
                    GetStringOrNull(ctx, "ShareAgreementNumber"),
                    ex.InnerException?.Message);
                errors.Add(new RowError(null, "apply_failed",
                    $"row {mr.SourceRowNumber}: {ex.Message}"));
            }
        } // end foreach row in sheetGroup
        } // end foreach sheetGroup

        _log.LogInformation(
            "RoomsForm.Apply: всего применено {Applied} строк из {Sheets} листов, ошибок: {Errors}",
            applied, rowsBySheet.Count, errors.Count);

        return new ApplyResult(applied, errors);
    }

    // ──────────────────────────── Helpers ──────────────────────────────────

    /// <summary>
    /// Резолвит имя листа («Квартиры», «Машиноместа», «Кладовые», …) в Title/ID
    /// из справочника RoomKind. Стратегии (по порядку):
    ///   1) точное совпадение `kindByTitle[sheetName]`;
    ///   2) совпадение нормализованных строк (lower + trim);
    ///   3) обрезка типичных русских plural-окончаний (а/я/ы/и) и повтор поиска.
    /// Возвращает (null, null) если ничего не подошло.
    /// </summary>
    private static (int? Id, string? Title) ResolveKindBySheetName(
        string sheetName, IDictionary<string, int> kindByTitle)
    {
        if (string.IsNullOrWhiteSpace(sheetName)) return (null, null);
        var name = sheetName.Trim();

        // 1. Прямое совпадение (case-insensitive благодаря StringComparer.OrdinalIgnoreCase в kindByTitle)
        if (kindByTitle.TryGetValue(name, out var id1))
            return (id1, FindMatchingTitle(name, kindByTitle));

        // 2. Plural-trimming heuristics для русского:
        //    «Квартиры» → «Квартир» / «Квартира»; «Машиноместа» → «Машиномест» / «Машиноместо»
        var candidates = new List<string>();
        if (name.Length > 1)
        {
            var last = name[^1];
            // Срезаем последнюю букву (а/я/ы/и/е) и пробуем
            if ("аяыиеёАЯЫИЕЁ".Contains(last))
                candidates.Add(name[..^1]);
            // Заменяем «ы» / «и» на «а» / «я» (обратное преобразование плюрала)
            if (last == 'ы') candidates.Add(name[..^1] + "а");
            if (last == 'и') candidates.Add(name[..^1] + "я");
            // «Машиноместа» → «Машиноместо» (а → о)
            if (last == 'а') candidates.Add(name[..^1] + "о");
        }
        foreach (var cand in candidates)
        {
            if (kindByTitle.TryGetValue(cand, out var id2))
                return (id2, FindMatchingTitle(cand, kindByTitle));
        }

        // Substring-fallback не используем сознательно: «Машиноместа» может
        // случайно совпасть с «Машино…» / «…меcт…» и т. п. Если plural-trim
        // не сработал — лучше потребовать явное «Тип/Название/Вид» в строке.
        return (null, null);
    }

    private static string? FindMatchingTitle(string title, IDictionary<string, int> kindByTitle)
    {
        // Возвращает оригинальный Title из словаря (с правильным регистром).
        foreach (var k in kindByTitle.Keys)
            if (string.Equals(k, title, StringComparison.OrdinalIgnoreCase)) return k;
        return title;
    }

    /// <summary>«Лит 1.1» → «1.1»; «корп 2» → «2»; «3.А» → «3»; «лит. 1» → «1».</summary>
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

    private static int? ParseNullableInt(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return int.TryParse(s.Replace(" ", string.Empty).Trim(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? GetStringOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetIntOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? GetDoubleOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
