using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    // Маппер зарегистрирован Singleton (общий регистр стратегий), а
    // RoomApplySnapshotStore зависит от Scoped ImportServiceDbContext —
    // открываем мини-scope через factory (см. FinModelImportMapper / BudgetVisaryUploader).
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Верхний потолок параллелизма на фазе 2 Apply (обработка Room+SA per Section).
    /// Не выкручиваем сильно, чтобы не перегружать Visary API параллельными запросами.
    /// Реальная степень = <c>min(N_sections, ProcessorCount, ParallelismCap)</c>.
    /// </summary>
    private const int ParallelismCap = 8;

    public RoomsFormImportMapper(
        ILogger<RoomsFormImportMapper> log,
        IListViewClient listView,
        ICrudClient crud,
        IServiceScopeFactory scopeFactory)
    {
        _log = log;
        _listView = listView;
        _crud = crud;
        _scopeFactory = scopeFactory;
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
        var skippedSheetsByKind = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheetName in dataRows.Select(r => r.Sheet).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (sId, sTitle) = ResolveKindBySheetName(sheetName, kindByTitle);
            sheetKindCache[sheetName] = (sId, sTitle);
            if (sId.HasValue)
            {
                _log.LogInformation(
                    "RoomsForm.Validate: лист '{Sheet}' → ожидаемый вид помещений '{Title}' (ID={Id})",
                    sheetName, sTitle, sId.Value);
            }
            else
            {
                // Имя листа не соответствует ни одному виду помещений из живого справочника
                // Visary (Квартира/Машиноместо/Кладовая/…). Такие листы — это «исторические
                // снапшоты» в пользовательских файлах: «Кв_01.04.26», «Кв_01.03.26 (2)», и т.п.
                // Они не реестр помещений; их обработка дала бы поток `required_missing`
                // на каждую строку и захламила бы отчёт. Пропускаем ВСЕ строки листа целиком.
                skippedSheetsByKind.Add(sheetName);
                _log.LogInformation(
                    "RoomsForm.Validate: лист '{Sheet}' пропущен — имя не соответствует ни одному " +
                    "RoomKind в справочнике Visary (Квартира/Машиноместо/Кладовая/…). " +
                    "Это нормально для исторических снапшотов («Кв_01.04.26» и т.п.).",
                    sheetName);
            }
        }

        // После определения «не наших» листов отфильтровываем их строки.
        if (skippedSheetsByKind.Count > 0)
        {
            var before = dataRows.Count;
            dataRows = dataRows.Where(r => !skippedSheetsByKind.Contains(r.Sheet)).ToList();
            _log.LogInformation(
                "RoomsForm.Validate: отфильтровано {Removed} строк из {SkippedSheets} листов, " +
                "не соответствующих RoomKind. Останется {Remaining} строк для дальнейшей валидации.",
                before - dataRows.Count, skippedSheetsByKind.Count, dataRows.Count);

            if (dataRows.Count == 0)
            {
                fileErrors.Add(new RowError(null, "no_data",
                    "В файле нет ни одного листа с именем, соответствующим виду помещений " +
                    "(Квартира/Машиноместо/Кладовая/Апартаменты/…). " +
                    $"Найденные листы: {string.Join(", ", skippedSheetsByKind.Select(s => $"'{s}'"))}."));
                return new ValidationResult([], fileErrors);
            }
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
                    row.Sheet ?? string.Empty,
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
                row.Sheet ?? string.Empty,
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
    //
    // Архитектура Apply (см. doc_project/96-rooms-incremental-parallel-apply.md):
    //   ① Pre-load snapshots — один SELECT по сайту, чтобы дифф-skip работал без БД-запросов на каждой строке.
    //   ② Pre-pass 1 — РНС в Site (один PATCH, как и раньше).
    //   ③ Pre-pass 2 — Sections sequential: для всех уникальных Section.Title сразу find-or-create.
    //                  Параллелить нельзя — две строки одной секции породили бы дубликат Section.
    //   ④ Pre-pass 3 — Developer link sequential: уникальные DeveloperPin → resolve org → create/link PM.
    //                  Тоже sequential: создание/привязка одной PM-записи не должна race-condition'ить с другой.
    //   ⑤ Main — Parallel.ForEachAsync по группам (Sheet, Section): каждая группа sequential внутри,
    //            группы между собой параллельно. На каждую строку:
    //              а) diff-hash → skip PATCH, если snapshot.MappedHash совпадает;
    //              б) Room find-or-create по уже-pre-loaded sectionId;
    //              в) ShareAgreement find/create как раньше;
    //              г) собираем RoomApplySnapshot в ConcurrentBag → один UpsertBatchAsync в конце.
    //
    // Счётчики `applied`/`skipped` — Interlocked. Журнал действий — ConcurrentDictionary<key, List<string>>
    // с локальным `lock(list)` на добавление: внешний lookup lock-free, внутренний short-burst для thread-safe Add.
    public async Task<ApplyResult> ApplyAsync(
        ImportContext context,
        VisaryDbContext visaryDb,
        IReadOnlyList<MappedRow> rows,
        CancellationToken ct)
    {
        var errors  = new ConcurrentBag<RowError>();
        int applied = 0;
        int skipped = 0;

        // Журнал действий per-row (Sheet, SourceRowNumber) → список меток.
        // ConcurrentDictionary даёт thread-safe GetOrAdd, но сам List<string>
        // мутируется под мини-локом на инстанс — это короткие burst-операции,
        // race-condition'а на разные ключи здесь нет.
        var actionsByRow = new ConcurrentDictionary<(string Sheet, int Row), List<string>>();
        void Log(string sheet, int row, string action)
        {
            var list = actionsByRow.GetOrAdd((sheet, row), _ => new List<string>(4));
            lock (list) list.Add(action);
        }

        if (context.VisarySiteId is null)
        {
            errors.Add(new RowError(null, "site_required",
                "Не указан объект строительства (visarySiteId)."));
            return new ApplyResult(0, errors.ToList());
        }

        var siteId = context.VisarySiteId.Value;
        int? projectId = context.VisaryProjectId;

        // ── ① Pre-load snapshots ─────────────────────────────────────────────
        // Маппер — Singleton, RoomApplySnapshotStore — Scoped (зависит от
        // ImportServiceDbContext). Открываем короткий scope ради одного SELECT
        // на сайт; внутри Parallel-цикла дёргать БД не будем — diff/skip целиком
        // в памяти.
        ConcurrentDictionary<RoomSnapshotKey, RoomApplySnapshot> snapshotsByKey;
        using (var loadScope = _scopeFactory.CreateScope())
        {
            var store = loadScope.ServiceProvider.GetRequiredService<RoomApplySnapshotStore>();
            snapshotsByKey = await store.LoadForSiteAsync(siteId, ct);
        }

        var validRows = rows.Where(mr => mr.IsValid).ToList();
        _log.LogInformation(
            "RoomsForm.Apply: siteId={SiteId}, validRows={Count}, snapshotsPreloaded={Snap}",
            siteId, validRows.Count, snapshotsByKey.Count);

        // ── ② Pre-pass: РНС в Site (как и раньше, один раз на сессию) ────────
        await TryUpdateSitePermissionNumberAsync(siteId, rows, ct);

        // ── ③ Pre-pass: Sections sequential ──────────────────────────────────
        // ConcurrentDictionary — основной цикл потом читает sectionId без блокировок.
        var sectionCache = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sectionTitlesNeeded = validRows
            .Select(mr =>
            {
                var v = mr.MappedValues.RootElement;
                return GetStringOrNull(v, "SectionTitleNumeric") ?? GetStringOrNull(v, "SectionTitle");
            })
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sectionTitle in sectionTitlesNeeded)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await _listView.GetSectionsBySiteAsync(siteId, sectionTitle, ct);
            var sectionTitleTrim = sectionTitle.Trim();
            var match = existing.Data.FirstOrDefault(x =>
                string.Equals((x.Title ?? string.Empty).Trim(), sectionTitleTrim,
                    StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                sectionCache[sectionTitle] = match.ID;
            }
            else
            {
                _log.LogInformation(
                    "RoomsForm.Apply: корпус не найден — создаём (siteId={SiteId}, title='{Title}')",
                    siteId, sectionTitle);
                var created = await _crud.CreateSectionAsync(new SectionCreateRequest
                {
                    ConstructionSiteID = siteId,
                    ConstructionSite   = new VisaryRef { ID = siteId },
                    Title              = sectionTitle,
                    Type               = new VisaryRef { ID = 3, Title = "МЖД" },
                }, ct);
                sectionCache[sectionTitle] = created.ID;
            }
        }

        // ── ④ Pre-pass: Developer link для уникальных PIN-ов ────────────────
        // Sequential: создание/привязка PM-записи не идемпотентна в смысле гонок.
        // Метод возвращает (возможно резолвлённый) projectId — нужен дальше для
        // CREATE ShareAgreement.
        projectId = await ResolveDeveloperLinksAsync(siteId, projectId, validRows, Log, ct);

        // ── ⑤ Main: Parallel.ForEachAsync по группам (Sheet, Section) ─────
        // Группа = (sheet, section). Внутри группы строки sequential — это
        // защищает Room.find-or-create от создания дубликатов при одинаковом
        // (Kind, RoomNumber, BuildingSection) в нескольких строках одной секции
        // (такого не должно быть в нормальном файле, но повторные строки в Excel
        // встречаются). Между группами — параллельно с потолком ParallelismCap.
        var groupsByKey = validRows
            .GroupBy(mr =>
            {
                var v = mr.MappedValues.RootElement;
                var sheet   = GetStringOrNull(v, "Sheet") ?? "<unknown>";
                var section = GetStringOrNull(v, "SectionTitleNumeric")
                              ?? GetStringOrNull(v, "SectionTitle") ?? string.Empty;
                return (Sheet: sheet, Section: section);
            })
            .ToList();

        var snapshotUpserts = new ConcurrentBag<RoomApplySnapshot>();
        var parallelism = groupsByKey.Count == 0 ? 1
            : Math.Min(Math.Min(ParallelismCap, Environment.ProcessorCount), groupsByKey.Count);

        _log.LogInformation(
            "RoomsForm.Apply: groups={Groups}, parallelism={P}",
            groupsByKey.Count, parallelism);

        await Parallel.ForEachAsync(groupsByKey,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (group, gct) =>
        {
            var sheetForRow = group.Key.Sheet;
            var sectionTitle = string.IsNullOrWhiteSpace(group.Key.Section) ? null : group.Key.Section;
            int? sectionId = sectionTitle is not null && sectionCache.TryGetValue(sectionTitle, out var sid)
                ? sid : (int?)null;

            // Один list-view запрос за все Room-ы секции — потом per-row только в памяти.
            List<global::Visary.Api.Dto.RoomRaw> roomsInSection = new();
            if (sectionId is not null)
            {
                try
                {
                    var fetched = await _listView.GetRoomsBySectionAsync(sectionId.Value, null, gct);
                    roomsInSection = fetched.Data.ToList();
                }
                catch (Exception fetchEx)
                {
                    _log.LogWarning(fetchEx,
                        "RoomsForm.Apply: не удалось загрузить помещения секции {SectionId}: {Msg}",
                        sectionId.Value, fetchEx.Message);
                }
            }

            foreach (var mr in group)
            {
                gct.ThrowIfCancellationRequested();
                var v = mr.MappedValues.RootElement;
                try
                {
                    var roomNumber = GetStringOrNull(v, "RoomNumber") ?? string.Empty;
                    var kindId = GetIntOrNull(v, "RoomKindId");
                    var buildingSection = GetStringOrNull(v, "BuildingSection") ?? string.Empty;

                    // ── (a) Diff-hash → skip, если snapshot совпал ───────────
                    var snapKey = RoomApplySnapshotStore.BuildKey(
                        siteId, sheetForRow, sectionTitle ?? string.Empty,
                        kindId, roomNumber, buildingSection);
                    var hash = RoomApplySnapshotStore.ComputeMappedHash(v);

                    if (snapshotsByKey.TryGetValue(snapKey, out var prev)
                        && string.Equals(prev.MappedHash, hash, StringComparison.Ordinal))
                    {
                        // Запись уже соответствует тому, что мы применили в прошлый раз —
                        // PATCH-и не нужны. Это и есть инкрементальный импорт.
                        Log(sheetForRow, mr.SourceRowNumber, "Без изменений — пропуск (snapshot)");
                        Interlocked.Increment(ref skipped);
                        Interlocked.Increment(ref applied);
                        continue;
                    }

                    if (sectionId is not null)
                    {
                        Log(sheetForRow, mr.SourceRowNumber, $"Корпус найден ({sectionTitle})");
                    }

                    // ── (b) Room find-or-create ──────────────────────────────
                    int? roomId = null;
                    if (sectionId is not null)
                    {
                        var roomNumberTrim = roomNumber.Trim();
                        var buildingSectionTrim = buildingSection.Trim();
                        var match = roomsInSection.FirstOrDefault(r =>
                            (kindId is null || r.Kind?.ID == kindId.Value)
                            && (string.Equals((r.ExplicationNumber ?? string.Empty).Trim(), roomNumberTrim, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals((r.Number            ?? string.Empty).Trim(), roomNumberTrim, StringComparison.OrdinalIgnoreCase))
                            && string.Equals(
                                    (r.BuildingSection ?? string.Empty).Trim(),
                                    buildingSectionTrim,
                                    StringComparison.OrdinalIgnoreCase));
                        roomId = match?.ID;
                    }

                    var roomKindTitle = GetStringOrNull(v, "RoomKindTitle") ?? string.Empty;
                    var uniqueNumber = $"{roomNumber}_{sectionTitle ?? string.Empty}_{buildingSection}";
                    var roomTitle = string.IsNullOrWhiteSpace(roomKindTitle)
                        ? uniqueNumber
                        : $"{roomKindTitle} {uniqueNumber}";

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
                        }, gct);
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
                        }, gct);
                        Log(sheetForRow, mr.SourceRowNumber, $"Помещение обновлено (№{roomNumber})");
                    }

                    // ── (c) ShareAgreement find/create ───────────────────────
                    var saNumber = GetStringOrNull(v, "ShareAgreementNumber");
                    int? saId = null;
                    if (!string.IsNullOrWhiteSpace(saNumber) && roomId is not null)
                    {
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

                        try
                        {
                            var byRoom = await _listView.GetShareAgreementsByRoomAsync(roomId.Value, null, gct);
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
                                    gct);

                                saMatch = found.Data
                                    .Where(a => string.Equals(
                                        (a.Number ?? string.Empty).Trim(), saNumberTrim,
                                        StringComparison.OrdinalIgnoreCase))
                                    .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
                                    .OrderByDescending(a => a.ID)
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
                            var saCreated = await _crud.CreateShareAgreementAsync(new ShareAgreementCreateRequest
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
                            }, gct);
                            saId = saCreated.ID;
                            Log(sheetForRow, mr.SourceRowNumber, $"ДДУ создан (№{saNumber})");
                        }
                        else
                        {
                            saId = saMatch.ID;
                            var isOrphan = saMatch.Room?.ID is null || saMatch.Room.ID != roomId.Value;
                            if (matchedInRoom)
                                Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден в помещении (не создан, №{saNumber})");
                            else if (isOrphan)
                            {
                                _log.LogInformation(
                                    "RoomsForm.Apply: найден орфанный/несоответствующий ДДУ id={SaId} number='{Num}' (Room={ExistingRoom}) — привязываем к roomId={NewRoom}",
                                    saMatch.ID, saNumber, saMatch.Room?.ID, roomId.Value);
                                Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден глобально (привязан к новому помещению, №{saNumber})");
                            }
                            else
                                Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден (не создан, №{saNumber})");

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
                            }, gct);
                        }
                    }

                    // ── (d) Snapshot для batch-upsert ────────────────────────
                    snapshotUpserts.Add(new RoomApplySnapshot
                    {
                        VisarySiteId           = siteId,
                        Sheet                  = sheetForRow,
                        SectionTitle           = sectionTitle ?? string.Empty,
                        RoomKindId             = kindId,
                        RoomNumber             = roomNumber,
                        BuildingSection        = buildingSection,
                        MappedHash             = hash,
                        MappedSnapshot         = JsonDocument.Parse(v.GetRawText()),
                        VisarySectionId        = sectionId,
                        VisaryRoomId           = roomId,
                        VisaryShareAgreementId = saId,
                        ShareAgreementNumber   = saNumber,
                        LastAppliedSessionId   = context.SessionId,
                        LastAppliedAt          = DateTimeOffset.UtcNow,
                    });
                    Interlocked.Increment(ref applied);
                }
                catch (Exception ex)
                {
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
            } // end foreach row in group
        }); // end Parallel.ForEachAsync

        // ── ⑥ Batch upsert snapshots — один SaveChanges на всё ───────────────
        if (!snapshotUpserts.IsEmpty)
        {
            using var upsertScope = _scopeFactory.CreateScope();
            var store = upsertScope.ServiceProvider.GetRequiredService<RoomApplySnapshotStore>();
            await store.UpsertBatchAsync(snapshotUpserts.ToList(), ct);
        }

        _log.LogInformation(
            "RoomsForm.Apply: применено {Applied}, из них skip-by-hash {Skipped}, групп {Groups}, ошибок {Errors}",
            applied, skipped, groupsByKey.Count, errors.Count);

        var rowActions = actionsByRow
            .Select(kv => new RowActionLog(kv.Key.Row, kv.Key.Sheet, kv.Value))
            .ToList();
        return new ApplyResult(applied, errors.ToList(), rowActions);
    }

    /// <summary>
    /// Pre-pass перед основным циклом Apply: одна итерация по всем уникальным
    /// <c>DeveloperPin</c> в валидных строках. Резолвит организацию, грузит
    /// существующие projectmanagement для сайта (один SELECT), и при отсутствии
    /// записи «Застройщик» — создаёт/переиспользует PM в рамках проекта и
    /// линкует к сайту.
    ///
    /// Sequential by design — параллельные CREATE projectmanagement c одинаковым
    /// orgId дали бы дубли в проекте.
    /// </summary>
    private async Task<int?> ResolveDeveloperLinksAsync(
        int siteId,
        int? projectId,
        IReadOnlyList<MappedRow> validRows,
        Action<string, int, string> Log,
        CancellationToken ct)
    {
        // Список (Sheet, SourceRowNumber, DeveloperPin) — нужен, чтобы метку
        // «Застройщик привязан к объекту» повесить на ту строку, где этот PIN
        // встретился впервые в файле. Это сохраняет совместимость с прежней
        // ленивой логикой: метки видны в построчном отчёте.
        var rowsWithPin = validRows
            .Select(mr =>
            {
                var v = mr.MappedValues.RootElement;
                return (
                    Sheet: GetStringOrNull(v, "Sheet") ?? "<unknown>",
                    Row:   mr.SourceRowNumber,
                    Pin:   GetStringOrNull(v, "DeveloperPin"));
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.Pin))
            .ToList();
        if (rowsWithPin.Count == 0) return projectId;

        var firstRowByPin = rowsWithPin
            .GroupBy(t => t.Pin!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Row).First(), StringComparer.OrdinalIgnoreCase);

        if (firstRowByPin.Count == 0) return projectId;

        // Один раз грузим существующие PM-записи сайта.
        var developerPmByOrg = new Dictionary<int, int>();
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
                "RoomsForm.Apply: pre-pass developers — загружено {Count} PM сайта, из них Застройщиков {Devs}",
                pmList.Data.Count, developerPmByOrg.Count);
        }
        catch (Exception loadEx)
        {
            _log.LogWarning(loadEx,
                "RoomsForm.Apply: не удалось загрузить projectmanagement для siteId={SiteId}: {Msg}",
                siteId, loadEx.Message);
        }

        foreach (var (pin, first) in firstRowByPin)
        {
            ct.ThrowIfCancellationRequested();

            int? orgId;
            try
            {
                var orgs = await _listView.GetOrganizationsByClientIdAsync(pin, ct);
                orgId = orgs.Data.FirstOrDefault()?.ID;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "RoomsForm.Apply: GetOrganizationsByClientId('{Pin}') не удался: {Msg}",
                    pin, ex.Message);
                continue;
            }
            if (orgId is null)
            {
                _log.LogWarning(
                    "RoomsForm.Apply: организация с ПИН '{Pin}' не найдена в Visary — пропуск привязки.",
                    pin);
                continue;
            }

            if (developerPmByOrg.ContainsKey(orgId.Value)) continue;

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
                continue;
            }

            int? reusablePmId = null;
            try
            {
                var inProject = await _listView.GetProjectManagementsByProjectAsync(
                    projectId.Value, orgId.Value, ProjectManagementRoles.Developer, ct);
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
                    pmIdToLink = existingPmId;
                    _log.LogInformation(
                        "RoomsForm.Apply: переиспользуем projectmanagement id={PmId} из projectId={ProjectId} для siteId={SiteId} (orgId={OrgId})",
                        existingPmId, projectId.Value, siteId, orgId.Value);
                    Log(first.Sheet, first.Row, "Застройщик переиспользован");
                }
                else
                {
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
                        "RoomsForm.Apply: создан Застройщик projectmanagement id={PmId} (orgId={OrgId}, projectId={ProjectId})",
                        created.ID, orgId.Value, projectId.Value);
                    Log(first.Sheet, first.Row, "Застройщик создан");
                }

                await _crud.LinkProjectManagementToSiteAsync(siteId, pmIdToLink, ct);
                developerPmByOrg[orgId.Value] = pmIdToLink;
                Log(first.Sheet, first.Row, "Застройщик привязан к объекту");
            }
            catch (Exception pmEx)
            {
                _log.LogWarning(pmEx,
                    "RoomsForm.Apply: не удалось привязать projectmanagement (orgId={OrgId}, siteId={SiteId}): {Msg}",
                    orgId.Value, siteId, pmEx.Message);
            }
        }

        return projectId;
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
