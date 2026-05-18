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

    /// <summary>
    /// Шапка файла помещений может стоять не в первой строке (например, в
    /// «Ежевика короткая 1.xlsx» строки 1–4 заняты заголовком «Реестр вывода КВАРТИР»,
    /// коэффициентами Кб=0,3/Кл=0,5 и подсказками; настоящие имена колонок —
    /// в строке 5, данные — с 8-й). Передаём парсеру список «опорных» заголовков:
    /// он просканирует первые ~30 строк и выберет ту, где найдётся ≥2 анкоров.
    /// </summary>
    public FileLayoutHint LayoutHint { get; } = new Tabular(HeaderAnchors: new[]
    {
        "ПИН застройщика",
        "Номер разрешения",
        "Номер проекта",
        "Этап",
        "Номер помещения/Квартира/Номер квартиры",
        "Тип/Название/Вид",
        "№ стр/корп",
        "Подъезд/Секция",
    });

    /// <summary>
    /// Значение <c>RoomKindRaw.RoomCategory</c>, означающее «Жилое» (Residential).
    /// Справочник Visary RoomCategory (нумерация с нуля):
    ///   0 = Residential (Квартира, Апартамент — единственная «жилая» категория)
    ///   1 = NonResidential
    ///   2 = ParkingPlace (Машиноместо)
    ///   3 = OtherNonResidential (Кладовая и т. п.)
    /// От значения зависит, какое поле площади заполняет маппер: для жилых —
    /// <c>ProjectArea</c>, для нежилых — <c>TotalArea</c> + <c>ProjectArea=0</c>.
    /// </summary>
    private const int ResidentialRoomCategory = 0;

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
    private static readonly string[] RoomsCountAliases       = [
        "Колич. комнат", "Колич комнат",       // встречается в реальных файлах (с точкой и без)
        "Количество комнат", "Кол-во комнат", "Кол. комнат",
        "Количество",                            // короткий заголовок
        "RoomsNumber"];
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
        // Карта kindId → RoomCategory. Нужна на Apply, чтобы для нежилых
        // помещений положить площадь в TotalArea и обнулить ProjectArea.
        var categoryByKindId = roomKindList.Data
            .ToDictionary(k => k.ID, k => k.RoomCategory);
        _log.LogInformation(
            "RoomsForm.Validate: загружен справочник RoomKind из Visary — {Count} записей: {Titles}",
            kindByTitle.Count, string.Join(", ", kindByTitle.Select(kv => $"{kv.Key}={kv.Value}")));
        _log.LogInformation(
            "RoomsForm.Validate: RoomCategory по Kind: {Categories}",
            string.Join(", ", roomKindList.Data
                .Select(k => $"{k.Title}={k.RoomCategory?.ToString() ?? "null"}")));

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

            // ── Тихий пропуск сводных/служебных строк ──────────────────────
            // Внутри листа «Квартира» (как в «Ежевика короткая 1.xlsx») сразу под
            // шапкой попадаются строки агрегатов: «ИТОГО», «Сумма с учетом вывода»,
            // «План», «Факт» — в первой колонке текст, остальные ячейки заполняются
            // формулами SUBTOTAL/SUMIF. Не считаем их данными: если ВСЕ три
            // идентификационных поля (НПС/РНС/Этап) пустые, строка не может
            // относиться к ОКС-у. Молча пропускаем — не порождая site_mismatch,
            // которым иначе захлёбывается отчёт.
            if (string.IsNullOrWhiteSpace(permission)
                && string.IsNullOrWhiteSpace(projectNum)
                && string.IsNullOrWhiteSpace(stageNumRaw))
            {
                _log.LogDebug(
                    "RoomsForm.Validate: row {Row} (sheet '{Sheet}') пропущена — нет НПС/РНС/Этапа (служебная/сводная строка).",
                    row.SourceRowNumber, row.Sheet);
                continue;
            }

            // ── Per-row сверка Site (НЕ ИЩЕМ Site, а ВАЛИДИРУЕМ выбранный) ─
            // Жёсткие проверки: НПС и Этап должны совпадать. Опционально РНС
            // (если пустой в файле — пропускаем эту проверку, как описано в задаче).
            var rowProjectNum = projectNum.Trim();
            int? rowStageNum  = ParseNullableInt(stageNumRaw);
            var rowPermission = permission.Trim();

            bool projectOk = string.Equals(rowProjectNum, siteProjectNumber, StringComparison.OrdinalIgnoreCase);
            bool stageOk   = rowStageNum.HasValue && siteStageNumber.HasValue
                          && rowStageNum.Value == siteStageNumber.Value;
            // РНС считаем совпадающим в трёх случаях:
            //   1) в файле РНС пустой — пропускаем проверку;
            //   2) равно тому, что уже стоит в Site;
            //   3) в самом Site РНС пустой — тогда непустой РНС из файла НЕ блокирует
            //      строку: после Validate в Apply мы один раз заполним РНС в ОКСе через
            //      PatchSiteAsync (см. шаг "Обновление РНС в Site" в room_sa_create.puml).
            bool permissionOk = string.IsNullOrWhiteSpace(rowPermission)
                          || string.Equals(rowPermission, sitePermissionNumber, StringComparison.OrdinalIgnoreCase)
                          || string.IsNullOrWhiteSpace(sitePermissionNumber);

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
            // Из значения извлекаем только цифры: «п1» → «1», «12А» → «12».
            // Если в файле остался текст вокруг числа, фиксируем в логе.
            var roomNumberRaw = ReadString(row, RoomNumberAliases);
            var roomNumber = ExtractDigitsOnly(roomNumberRaw);
            if (string.IsNullOrWhiteSpace(roomNumber))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomNumberAliases), "required_missing",
                    "Не указан номер помещения."));
            }
            else if (!string.Equals(roomNumberRaw, roomNumber, StringComparison.Ordinal))
            {
                _log.LogDebug(
                    "RoomsForm.Validate: row {Row} — номер помещения '{Raw}' нормализован в '{Numeric}' (удалены не-цифры).",
                    row.SourceRowNumber, roomNumberRaw, roomNumber);
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

            // «Колич. комнат» нередко приходит в свободной форме: «1 к.», «1 к», «п1»,
            // «1п», «2-к», «3 ком.», «студия». Берём ПЕРВУЮ непрерывную группу цифр —
            // это и есть число комнат. «студия»/прочерк/пусто → null. Жёсткое
            // int.TryParse тут не годится: пользователю не должно прилетать
            // invalid_number на «1 к.» — это валидная однушка в реальных реестрах.
            var roomsCountRaw = ReadString(row, RoomsCountAliases);
            int? roomsCount = ExtractFirstRunOfDigits(roomsCountRaw);
            if (roomsCount.HasValue
                && !string.Equals(roomsCountRaw, roomsCount.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                _log.LogDebug(
                    "RoomsForm.Validate: row {Row} — «Колич. комнат» '{Raw}' нормализовано в {N} (вытащена ведущая цифровая группа).",
                    row.SourceRowNumber, roomsCountRaw, roomsCount.Value);
            }
            // Если raw непустой, но числа нет — пользователь написал «студия»/«—» и т.п.
            // Не считаем ошибкой числа: оставляем null. required_missing-проверка ниже
            // (только если Kind=Квартира) подскажет, если для квартиры реально нужна цифра.

            // Если вид помещения «Квартира» — «Количество комнат» обязательно.
            if (roomsCount is null
                && !string.IsNullOrWhiteSpace(roomKindTitle)
                && string.Equals(roomKindTitle.Trim(), "Квартира", StringComparison.OrdinalIgnoreCase))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomsCountAliases), "required_missing",
                    "Не указано количество комнат для квартиры."));
            }

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

            // Категория Kind (residential/non-residential) — нужна Apply, чтобы
            // решить, в какое поле положить площадь.
            int? roomCategory = (kindId != 0 && categoryByKindId.TryGetValue(kindId, out var cat))
                ? cat
                : null;

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
                ["RoomCategory"]         = roomCategory,
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

        // Журнал действий per-row: ключ (Sheet, SourceRowNumber). Записываем сюда
        // человекочитаемые метки («Корпус создан», «Помещение обновлено»,
        // «ДДУ найден (не создан)», …) по ходу Apply. На выходе превращаем в
        // RowActionLog-список для ApplyResult; Pipeline сохранит как StagedRow.Actions.
        var actionsByRow = new Dictionary<(string Sheet, int Row), List<string>>();
        void Log(string sheet, int row, string action)
        {
            var key = (sheet, row);
            if (!actionsByRow.TryGetValue(key, out var list))
                actionsByRow[key] = list = new List<string>(4);
            list.Add(action);
        }

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
        // (orgId) → projectmanagement.ID c ролью «Застройщик» (после проверки/создания
        // привязки на этой сессии). Один раз за сессию читаем список PM сайта; дальше
        // — только локальные мутации.
        var developerPmByOrg = new Dictionary<int, int>();
        var pmListLoaded = false;

        // ProjectID нужен для CREATE projectmanagement (Project — обязателен в Visary).
        // Берём из контекста, иначе резолвим через свежий Site → Project.ID.
        int? projectId = context.VisaryProjectId;

        // ⚙️ Группируем строки по листу (один лист = один тип помещений).
        // GroupBy в LINQ сохраняет порядок появления групп = порядок листов в файле.
        var rowsBySheet = rows
            .Where(mr => mr.IsValid)
            .GroupBy(mr => GetStringOrNull(mr.MappedValues.RootElement, "Sheet") ?? "<unknown>",
                     StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── 0. Обновление РНС в Site, если в ОКСе он пустой ────────────────
        // Шаг из room_sa_create.puml: «Если в Объекте нет РНС → обновить значение
        // РНС в Объекте строительства». Делаем один раз на сессию: берём первое
        // непустое значение из валидных строк, читаем свежий RowVersion и PATCH-аем.
        await TryUpdateSitePermissionNumberAsync(siteId, rows, ct);

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
            var sheetForRow = sheetGroup.Key;

            try
            {
                // ── 1. Организация-застройщик через ProjectManagement ────────
                //
                // Flow по доке 75-projectmanagement-developer-link.md:
                //   1) PIN → ID организации (listview/organization, ClientID=...)
                //   2) Список projectmanagement сайта (listview/constructionsite/manytomany/projectmanagement)
                //   3) Если уже есть PM c этой Organization и Role=Застройщик → пропуск
                //   4) Иначе CREATE projectmanagement + LINK к сайту
                //
                // Кэшируется per-session: список PM грузится один раз, далее — локально.
                var devPin = GetStringOrNull(v, "DeveloperPin");
                if (!string.IsNullOrWhiteSpace(devPin))
                {
                    if (!orgCache.TryGetValue(devPin, out var orgId))
                    {
                        var orgs = await _listView.GetOrganizationsByClientIdAsync(devPin, ct);
                        orgId = orgs.Data.FirstOrDefault()?.ID;
                        orgCache[devPin] = orgId;
                        if (orgId is null)
                        {
                            _log.LogWarning(
                                "RoomsForm.Apply: организация с ПИН '{Pin}' не найдена в Visary — пропуск привязки.",
                                devPin);
                        }
                    }

                    if (orgId is not null)
                    {
                        // Один раз за сессию — прочитать существующие PM для сайта.
                        if (!pmListLoaded)
                        {
                            try
                            {
                                var pmList = await _listView.GetProjectManagementsBySiteAsync(siteId, ct);
                                foreach (var pm in pmList.Data)
                                {
                                    if (pm.Organization?.ID is int existingOrgId
                                        && pm.Role?.ID == ProjectManagementRoles.Developer)
                                    {
                                        developerPmByOrg[existingOrgId] = pm.ID;
                                    }
                                }
                                _log.LogInformation(
                                    "RoomsForm.Apply: загружено {Count} существующих projectmanagement-записей для siteId={SiteId} (из них Застройщиков с организацией: {Devs})",
                                    pmList.Data.Count, siteId, developerPmByOrg.Count);
                            }
                            catch (Exception loadEx)
                            {
                                // Не блокируем импорт — попробуем создавать «слепо» (Visary вернёт 4xx при дубликате).
                                _log.LogWarning(loadEx,
                                    "RoomsForm.Apply: не удалось загрузить projectmanagement для siteId={SiteId}: {Msg}",
                                    siteId, loadEx.Message);
                            }
                            pmListLoaded = true;
                        }

                        // Уже есть Застройщик с этой организацией на этом сайте — пропуск.
                        if (!developerPmByOrg.ContainsKey(orgId.Value))
                        {
                            // Резолвим projectId один раз — нужен и для поиска по проекту, и для CREATE.
                            if (projectId is null)
                            {
                                try
                                {
                                    var siteFull = await _crud.GetSiteByIdFullAsync(siteId, ct);
                                    projectId = siteFull.Project?.ID;
                                }
                                catch (Exception siteEx)
                                {
                                    _log.LogWarning(siteEx,
                                        "RoomsForm.Apply: не удалось получить Project из Site {SiteId}: {Msg}",
                                        siteId, siteEx.Message);
                                }
                            }

                            if (projectId is null)
                            {
                                _log.LogWarning(
                                    "RoomsForm.Apply: пропуск projectmanagement (orgId={OrgId}) — не удалось определить projectId.",
                                    orgId.Value);
                            }
                            else
                            {
                                // (a) Поиск PM (orgId, Role=Застройщик) в рамках всего проекта —
                                //     возможно, уже создан для соседнего объекта того же проекта.
                                //     При нескольких подходящих — берём с наибольшим ID (свежайший).
                                int? reusablePmId = null;
                                try
                                {
                                    var inProject = await _listView.GetProjectManagementsByProjectAsync(
                                        projectId.Value, orgId.Value, ProjectManagementRoles.Developer, ct);

                                    // Сервер уже отфильтровал по Organization+Role, но Visary иногда
                                    // отдаёт «лишние» записи (contains может матчить по подстроке) —
                                    // отстрахуем себя локальной фильтрацией перед взятием max ID.
                                    reusablePmId = inProject.Data
                                        .Where(pm => pm.Organization?.ID == orgId.Value
                                                     && pm.Role?.ID == ProjectManagementRoles.Developer)
                                        .OrderByDescending(pm => pm.ID)
                                        .FirstOrDefault()?.ID;
                                }
                                catch (Exception listEx)
                                {
                                    _log.LogWarning(listEx,
                                        "RoomsForm.Apply: поиск projectmanagement в проекте {ProjectId} не удался: {Msg}",
                                        projectId.Value, listEx.Message);
                                }

                                try
                                {
                                    int pmIdToLink;
                                    if (reusablePmId is int existingPmId)
                                    {
                                        // (b) Нашли — переиспользуем существующую PM-запись.
                                        pmIdToLink = existingPmId;
                                        _log.LogInformation(
                                            "RoomsForm.Apply: переиспользуем projectmanagement id={PmId} из projectId={ProjectId} для siteId={SiteId} (orgId={OrgId})",
                                            existingPmId, projectId.Value, siteId, orgId.Value);
                                        Log(sheetForRow, mr.SourceRowNumber, "Застройщик переиспользован");
                                    }
                                    else
                                    {
                                        // (c) В проекте нет — создаём новую запись.
                                        var created = await _crud.CreateProjectManagementAsync(
                                            new ProjectManagementCreateRequest
                                            {
                                                Project = new VisaryRef { ID = projectId.Value },
                                                Organization = new VisaryRef { ID = orgId.Value },
                                                Role = new VisaryRef
                                                {
                                                    ID = ProjectManagementRoles.Developer,
                                                    Title = ProjectManagementRoles.DeveloperTitle,
                                                },
                                                Affiliation = 0,
                                            }, ct);
                                        pmIdToLink = created.ID;
                                        _log.LogInformation(
                                            "RoomsForm.Apply: создан новый Застройщик projectmanagement id={PmId} (orgId={OrgId}, projectId={ProjectId})",
                                            created.ID, orgId.Value, projectId.Value);
                                        Log(sheetForRow, mr.SourceRowNumber, "Застройщик создан");
                                    }

                                    // (d) Линкуем найденную/созданную PM с сайтом.
                                    await _crud.LinkProjectManagementToSiteAsync(siteId, pmIdToLink, ct);
                                    developerPmByOrg[orgId.Value] = pmIdToLink;
                                    Log(sheetForRow, mr.SourceRowNumber, "Застройщик привязан к объекту");
                                }
                                catch (Exception pmEx)
                                {
                                    _log.LogWarning(pmEx,
                                        "RoomsForm.Apply: не удалось привязать projectmanagement (orgId={OrgId}, siteId={SiteId}): {Msg}",
                                        orgId.Value, siteId, pmEx.Message);
                                }
                            }
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
                        // PRE-CHECK дубликатов корпуса: ищем по Title с Trim()+OrdinalIgnoreCase.
                        // Без Trim «1.1» из файла и «1.1 » в БД считались разными — на второй
                        // импорт создавался дубликат. Visary listview-фильтр уже использует
                        // `contains`, локальный фильтр приводит набор к точному совпадению.
                        var existing = await _listView.GetSectionsBySiteAsync(siteId, sectionTitle, ct);
                        var sectionTitleTrim = sectionTitle.Trim();
                        var match = existing.Data.FirstOrDefault(x =>
                            string.Equals((x.Title ?? string.Empty).Trim(), sectionTitleTrim,
                                StringComparison.OrdinalIgnoreCase));
                        if (match is not null)
                        {
                            cached = match.ID;
                            Log(sheetForRow, mr.SourceRowNumber, $"Корпус найден ({sectionTitle})");
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
                            Log(sheetForRow, mr.SourceRowNumber, $"Корпус создан ({sectionTitle})");
                        }
                        sectionCache[sectionTitle] = cached;
                    }
                    else
                    {
                        // Корпус уже встречался в этой сессии — кэш-хит, реального
                        // вызова Visary не было, но из перспективы пользователя
                        // строка всё равно «привязалась к корпусу».
                        Log(sheetForRow, mr.SourceRowNumber, $"Корпус найден ({sectionTitle})");
                    }
                    sectionId = cached;
                }

                // ── 3. Room: найти/создать ──────────────────────────────────
                // Уникальность Room — в разрезе Section × Kind × Number × BuildingSection.
                // В одной секции могут одновременно жить квартира №3, машиноместо №3,
                // кладовая №3 — это РАЗНЫЕ помещения (Kind различен).
                // Кроме того, два помещения с одним номером, но в разных подъездах
                // («Подъезд/Секция» в файле) — также РАЗНЫЕ. Без проверки
                // BuildingSection импорт PATCH-ил бы первое попавшееся и «терял»
                // все последующие строки с тем же номером в других подъездах.
                var roomNumber = GetStringOrNull(v, "RoomNumber") ?? string.Empty;
                var kindId = GetIntOrNull(v, "RoomKindId");
                var buildingSection = GetStringOrNull(v, "BuildingSection") ?? string.Empty;
                int? roomId = null;
                if (sectionId is not null)
                {
                    // PRE-CHECK дубликатов помещения: Section × Kind × Number × BuildingSection.
                    // Все строковые поля нормализуем через Trim()+OrdinalIgnoreCase — пробелы
                    // в Excel-ячейках и хвостовые символы в Visary иначе обходят дедуп.
                    var roomsInSection = await _listView.GetRoomsBySectionAsync(sectionId.Value, null, ct);
                    var roomNumberTrim = roomNumber.Trim();
                    var buildingSectionTrim = buildingSection.Trim();
                    var match = roomsInSection.Data.FirstOrDefault(r =>
                        (kindId is null || r.Kind?.ID == kindId.Value)
                        && (string.Equals((r.ExplicationNumber ?? string.Empty).Trim(), roomNumberTrim, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals((r.Number            ?? string.Empty).Trim(), roomNumberTrim, StringComparison.OrdinalIgnoreCase))
                        && string.Equals(
                                (r.BuildingSection ?? string.Empty).Trim(),
                                buildingSectionTrim,
                                StringComparison.OrdinalIgnoreCase));
                    roomId = match?.ID;
                }
                // Формат UniqueNumber / Title по контракту импорта:
                //   UniqueNumber = ExplicationNumber + "_" + Section.Title + "_" + BuildingSection
                //                  → «15/16_1.1_1»
                //   Title        = Kind.Title + " " + UniqueNumber
                //                  → «Машиноместо 15/16_1.1_1»
                // Пустые сегменты могут давать «висящие» подчёркивания/пробелы, но
                // схема выдержки чисел сохраняется: пользователь хочет видеть позицию
                // секции/корпуса даже когда её нет в источнике (это будет сигналом
                // о неполных входных данных).
                var roomKindTitle = GetStringOrNull(v, "RoomKindTitle") ?? string.Empty;
                var uniqueNumber = $"{roomNumber}_{sectionTitle ?? string.Empty}_{buildingSection}";
                var roomTitle = string.IsNullOrWhiteSpace(roomKindTitle)
                    ? uniqueNumber
                    : $"{roomKindTitle} {uniqueNumber}";

                // Площадь: для жилых (RoomCategory == 1) — в ProjectArea, как раньше.
                // Для нежилых (Машиноместо / Кладовая / Коммерческое / …) —
                // площадь в TotalArea, а ProjectArea = 0. Если категория Kind не
                // пришла (null), оставляем дефолт «как для жилого», чтобы случайно
                // не перенести площадь в неправильное поле на незнакомых Kind.
                var areaFromFile = GetDoubleOrNull(v, "ProjectArea");
                var roomCategory = GetIntOrNull(v, "RoomCategory");
                var isNonResidential = roomCategory.HasValue && roomCategory.Value != ResidentialRoomCategory;
                double? projectAreaForCrud = isNonResidential ? 0d : areaFromFile;
                double? totalAreaForCrud   = isNonResidential ? areaFromFile : null;

                if (roomId is null)
                {
                    var created = await _crud.CreateRoomAsync(new RoomCreateRequest
                    {
                        SiteID            = siteId,
                        Site              = new VisaryRef { ID = siteId },
                        Title             = roomTitle,
                        ExplicationNumber = roomNumber,
                        UniqueNumber      = uniqueNumber,
                        Section           = sectionId is null ? null : new VisaryRef { ID = sectionId.Value },
                        Kind              = kindId    is null ? null : new VisaryRef { ID = kindId.Value },
                        Floor             = GetStringOrNull(v, "Floor"),
                        BuildingSection   = buildingSection,
                        RoomsNumber       = GetIntOrNull(v, "RoomsCount"),
                        ProjectArea       = projectAreaForCrud,
                        TotalArea         = totalAreaForCrud,
                        CostForOne        = GetDoubleOrNull(v, "CostForOne"),
                        MarketCostPerM    = GetDoubleOrNull(v, "MarketCostPerM"),
                        ZalogCostPerM     = GetDoubleOrNull(v, "ZalogCostPerM"),
                    }, ct);
                    roomId = created.ID;
                    Log(sheetForRow, mr.SourceRowNumber, $"Помещение создано (№{roomNumber})");
                }
                else
                {
                    await _crud.PatchRoomAsync(roomId.Value, new RoomPatchRequest
                    {
                        Title           = roomTitle,
                        UniqueNumber    = uniqueNumber,
                        Section         = sectionId is null ? null : new VisaryRef { ID = sectionId.Value },
                        Kind            = kindId    is null ? null : new VisaryRef { ID = kindId.Value },
                        Floor           = GetStringOrNull(v, "Floor"),
                        BuildingSection = buildingSection,
                        RoomsNumber     = GetIntOrNull(v, "RoomsCount"),
                        ProjectArea     = projectAreaForCrud,
                        TotalArea       = totalAreaForCrud,
                        CostForOne      = GetDoubleOrNull(v, "CostForOne"),
                        MarketCostPerM  = GetDoubleOrNull(v, "MarketCostPerM"),
                        ZalogCostPerM   = GetDoubleOrNull(v, "ZalogCostPerM"),
                    }, ct);
                    Log(sheetForRow, mr.SourceRowNumber, $"Помещение обновлено (№{roomNumber})");
                }

                // ── 4. ShareAgreement: глобально найти / реанимировать / создать ──
                //
                // Раньше искали ДДУ только в пределах комнаты — это пропускало
                // «орфанные» ДДУ, которые есть в Visary, но не привязаны к Room.
                // Симптом: повторный импорт создавал дубликат ДДУ (см. скриншот в
                // doc 76-share-agreement-dedup.md).
                //
                // Теперь: ищем глобально по (Number, RoomKindRef, ConditionalNumber,
                // StageNumber, ProjectNumber) — это уникальный бизнес-ключ ДДУ.
                // Если найдено несколько — берём max(ID). Орфанный ДДУ
                // PATCH'им на текущую комнату/сайт/проект. Иначе — создаём.
                var saNumber = GetStringOrNull(v, "ShareAgreementNumber");
                if (!string.IsNullOrWhiteSpace(saNumber) && roomId is not null)
                {
                    // StageNumber как строка для фильтра/CREATE/PATCH (см. ниже).
                    var stageNumberForSa = GetStringOrNull(v, "StageNumberRaw");
                    if (string.IsNullOrWhiteSpace(stageNumberForSa))
                    {
                        var stageInt = GetIntOrNull(v, "StageNumber");
                        stageNumberForSa = stageInt?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    var projectNumberForSa = GetStringOrNull(v, "ProjectNumber");

                    var saNumberTrim = saNumber.Trim();
                    ShareAgreementRaw? saMatch = null;
                    bool matchedInRoom = false;

                    // (1) PRE-CHECK В КОМНАТЕ — самая частая причина дубликатов:
                    //     повторный импорт того же файла или похожих строк в ту же
                    //     комнату. Тянем ВСЕ ДДУ комнаты (без серверного фильтра по
                    //     Number — Visary `=` чувствителен к whitespace/case), затем
                    //     локально сравниваем Number с Trim()+OrdinalIgnoreCase.
                    //     Это надёжнее серверного фильтра и спасает от дубликатов
                    //     вроде «№ маш 2 -1-3» / «№ маш 2 -1-3 » (хвостовой пробел).
                    try
                    {
                        var byRoom = await _listView.GetShareAgreementsByRoomAsync(roomId.Value, null, ct);
                        saMatch = byRoom.Data
                            .Where(a => string.Equals(
                                (a.Number ?? string.Empty).Trim(), saNumberTrim,
                                StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(a => a.ID)
                            .FirstOrDefault();
                        if (saMatch is not null) matchedInRoom = true;
                    }
                    catch (Exception roomFindEx)
                    {
                        _log.LogWarning(roomFindEx,
                            "RoomsForm.Apply: pre-check ДДУ в комнате roomId={RoomId} не удался: {Msg} — попробуем глобальный поиск.",
                            roomId.Value, roomFindEx.Message);
                    }

                    // (2) Если в комнате нет — глобальный поиск по бизнес-ключу.
                    //     Так находим orphan-ДДУ (созданные где-то ещё с тем же
                    //     номером/проектом/этапом/комнатой-условной) и привязываем
                    //     к нашей комнате вместо плодения нового.
                    if (saMatch is null)
                    {
                        try
                        {
                            var found = await _listView.FindShareAgreementsAsync(
                                number:            saNumber,
                                roomKindId:        kindId,
                                conditionalNumber: roomNumber,
                                stageNumber:       stageNumberForSa,
                                projectNumber:     projectNumberForSa,
                                ct);

                            // Локальный пост-фильтр: Visary `contains` для VisaryRef может матчить
                            // подстроку шире нужного — отстрахуем себя по точным значениям с Trim().
                            saMatch = found.Data
                                .Where(a => string.Equals(
                                    (a.Number ?? string.Empty).Trim(), saNumberTrim,
                                    StringComparison.OrdinalIgnoreCase))
                                .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
                                .OrderByDescending(a => a.ID)   // max(ID) при нескольких подходящих
                                .FirstOrDefault();
                        }
                        catch (Exception findEx)
                        {
                            _log.LogWarning(findEx,
                                "RoomsForm.Apply: глобальный поиск ДДУ '{Number}' не удался: {Msg} — будет создан новый.",
                                saNumber, findEx.Message);
                        }
                    }

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
                            ProjectNumber     = projectNumberForSa,
                            StageNumber       = stageNumberForSa,
                            ConditionalNumber = roomNumber,
                        }, ct);
                        Log(sheetForRow, mr.SourceRowNumber, $"ДДУ создан (№{saNumber})");
                    }
                    else
                    {
                        var isOrphan = saMatch.Room?.ID is null || saMatch.Room.ID != roomId.Value;
                        if (matchedInRoom)
                        {
                            // Pre-check в комнате попал — это самый частый кейс:
                            // повторный импорт того же файла. Просто PATCH-им поля.
                            Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден в помещении (не создан, №{saNumber})");
                        }
                        else if (isOrphan)
                        {
                            _log.LogInformation(
                                "RoomsForm.Apply: найден орфанный/несоответствующий ДДУ id={SaId} number='{Num}' (Room={ExistingRoom}) — привязываем к roomId={NewRoom}",
                                saMatch.ID, saNumber, saMatch.Room?.ID, roomId.Value);
                            Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден глобально (привязан к новому помещению, №{saNumber})");
                        }
                        else
                        {
                            Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден (не создан, №{saNumber})");
                        }

                        await _crud.PatchShareAgreementAsync(saMatch.ID, new ShareAgreementPatchRequest
                        {
                            Number            = saNumber,
                            Title             = saNumber,
                            Site              = new VisaryRef { ID = siteId },
                            Project           = context.VisaryProjectId is null
                                                    ? null
                                                    : new VisaryRef { ID = context.VisaryProjectId.Value },
                            RoomID            = roomId.Value,
                            Room              = new VisaryRef { ID = roomId.Value },
                            RoomKindRef       = kindId is null ? null : new VisaryRef { ID = kindId.Value },
                            ConditionalNumber = roomNumber,
                            StageNumber       = stageNumberForSa,
                            ProjectNumber     = projectNumberForSa,
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

        var rowActions = actionsByRow
            .Select(kv => new RowActionLog(kv.Key.Row, kv.Key.Sheet, kv.Value))
            .ToList();
        return new ApplyResult(applied, errors, rowActions);
    }

    /// <summary>
    /// Если в Site (выбранном ОКСе) поле <c>ConstructionPermissionNumber</c> пустое,
    /// а в одной из валидных строк файла РНС указан — заполняем его в Visary через
    /// <see cref="ICrudClient.PatchSiteAsync"/>. Выполняется один раз на сессию.
    ///
    /// Используется свежий RowVersion (повторный <c>GetSiteByIdFullAsync</c>), а не тот,
    /// что был получен в Validate, — между Validate и Apply Site могли поменять извне.
    /// При расхождении РНС между строками — берём первый и логируем warn.
    /// </summary>
    private async Task TryUpdateSitePermissionNumberAsync(
        int siteId, IReadOnlyList<MappedRow> rows, CancellationToken ct)
    {
        var permissionsInFile = rows
            .Where(mr => mr.IsValid)
            .Select(mr => GetStringOrNull(mr.MappedValues.RootElement, "PermissionNumber"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (permissionsInFile.Count == 0) return;

        ConstructionSiteFull current;
        try
        {
            current = await _crud.GetSiteByIdFullAsync(siteId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "RoomsForm.Apply: не удалось перечитать Site {SiteId} для проверки РНС: {Msg}",
                siteId, ex.Message);
            return;
        }

        var sitePermission = (current.ConstructionPermissionNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(sitePermission))
        {
            // Уже не пустой — значит либо был с самого начала, либо кто-то заполнил
            // параллельно. В этом случае ничего не делаем; рассинхрон со строкой файла
            // (если значения отличаются) — на совести пользователя, лог-предупреждение.
            var divergent = permissionsInFile
                .Where(p => !string.Equals(p, sitePermission, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (divergent.Count > 0)
            {
                _log.LogWarning(
                    "RoomsForm.Apply: РНС в Site уже задан '{Site}', но в файле встречаются другие значения: {Divergent}. PATCH не выполняется.",
                    sitePermission, string.Join(", ", divergent));
            }
            return;
        }

        var candidate = permissionsInFile[0];
        if (permissionsInFile.Count > 1)
        {
            _log.LogWarning(
                "RoomsForm.Apply: в файле встретилось несколько разных РНС: {All}. Будет применён первый — '{Pick}'.",
                string.Join(", ", permissionsInFile), candidate);
        }

        try
        {
            await _crud.PatchSiteAsync(siteId, new SitePatchRequest
            {
                RowVersion                   = current.RowVersion,
                ConstructionPermissionNumber = candidate,
            }, ct);
            _log.LogInformation(
                "RoomsForm.Apply: РНС обновлён в Site {SiteId}: '' → '{New}'",
                siteId, candidate);
        }
        catch (Exception ex)
        {
            // Не блокируем импорт: помещения создавать всё равно можно. Visary
            // optimistic-locking может вернуть 409, если RowVersion устарел; в этом
            // случае пользователь может перезапустить импорт.
            _log.LogWarning(ex,
                "RoomsForm.Apply: PATCH Site {SiteId} (РНС='{New}') не удался: {Msg}",
                siteId, candidate, ex.Message);
        }
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

    /// <summary>
    /// Возвращает первую непрерывную последовательность цифр как <c>int</c>.
    /// Поведение на примерах: «1 к.» → 1; «п1» → 1; «1п» → 1; «10 к» → 10;
    /// «студия» → <c>null</c>; пусто → <c>null</c>; «1 к. 2» → 1 (берём
    /// ПЕРВЫЙ run, не клеим разрозненные цифры).
    /// Используется для «Колич. комнат», чтобы значения вроде «1 к.» / «п1»
    /// корректно превращались в 1, а не отвергались как «не число».
    /// </summary>
    internal static int? ExtractFirstRunOfDigits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) break;
        }
        if (sb.Length == 0) return null;
        return int.TryParse(sb.ToString(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
    }

    /// <summary>«п1» → «1»; «12А» → «12»; «кв. 7» → «7»; «—» → <c>""</c>.
    /// Игнорирует все символы кроме цифр (включая точки/запятые).</summary>
    private static string ExtractDigitsOnly(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return new string(raw.Where(char.IsDigit).ToArray());
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
