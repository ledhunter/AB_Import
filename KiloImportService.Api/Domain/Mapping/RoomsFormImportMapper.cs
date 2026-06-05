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
/// Контракт (см. doc_project/101-rooms-multi-site-by-project.md):
/// пользователь выбирает <c>Project</c> в UI (Site НЕ выбирает —
/// для одного файла может быть N разных ОКС). Маппер для каждой строки
/// файла резолвит Site внутри проекта по ключам (<c>ConstructionProjectNumber</c>, <c>StageNumber</c>)
/// через <see cref="IListViewClient.GetSitesByProjectAndKeysAsync"/> с
/// <c>Filter [["ConstructionProjectNumber","=",X],"and",["StageNumber","=",Y]]</c>.
///
/// Резолв ситуации:
///   • 1 кандидат — ID сохраняется в <c>MappedValues.SiteId</c>, строка валидна.
///   • 0 кандидатов — row-error <c>site_not_found_in_project</c>.
///   • >1 кандидатов — row-error <c>site_ambiguous</c> со списком ID.
/// РНС из файла больше не блокирует строку — раз Site однозначно резолвится по (НПС,Этап),
/// расхождение РНС идёт в Debug-лог. PATCH РНС в Site (если в Site пусто) остаётся в Apply.
///
/// Apply группирует валидные строки по SiteId; внутри одного Site flow совпадает с
/// прежним (snapshot diff-skip, sections find-or-create, parallel по (Sheet, Section) —
/// см. doc_project/96-rooms-incremental-parallel-apply.md).
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

    /// <summary>
    /// Результат резолва Site по паре (НПС, Этап) внутри проекта. Один экземпляр на
    /// уникальную пару — кэш строится в ValidateAsync pre-pass, чтобы не дёргать
    /// Visary N×N раз на одну и ту же пару из соседних строк.
    /// </summary>
    /// <param name="Matches">Найденные кандидаты (0 → site_not_found_in_project, 1 → OK, >1 → site_ambiguous).</param>
    /// <param name="Error">Текст ошибки сети/Visary, если listview упал. <c>null</c> при успехе.</param>
    private sealed record SiteResolution(IReadOnlyList<ConstructionSiteRaw> Matches, string? Error = null);

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
    /// <summary>
    /// Колонка «Общая площадь» в файлах нежилых помещений (машиноместо/кладовая/нежилое).
    /// Для жилых берётся <see cref="ProjectAreaAliases"/>; для нежилых эта колонка
    /// уходит в Visary как <c>TotalArea</c>. Без неё нежилые помещения не получали
    /// ни ProjectArea (колонка пустая), ни TotalArea (поля для маппинга не было).
    ///
    /// Заголовки наблюдались в разных вариантах: с/без «Общая», с/без точки,
    /// с/без запятой. Сравнение в <c>ReadString</c> точное (case-insensitive),
    /// поэтому каждую форму перечисляем отдельно. «Площадь, кв.м» (без «Общая»)
    /// — реальный заголовок в Репино-Парк, машиноместо лист.
    /// </summary>
    private static readonly string[] TotalAreaAliases        = [
        "Общая площадь, кв.м.", "Общая площадь, кв.м", "Общая площадь",
        "Площадь, кв.м.", "Площадь, кв.м", "Площадь кв.м.", "Площадь кв.м",
        "TotalArea"];

    /// <summary>
    /// Маркеры Excel-формул, выпавших с ошибкой: <c>#N/A</c>, <c>#REF!</c>, <c>#VALUE!</c>,
    /// <c>#NAME?</c>, <c>#NUM!</c>, <c>#DIV/0!</c>, <c>#NULL!</c>, <c>#GETTING_DATA</c>.
    /// Пользователи иногда оставляют такие значения в столбце «№ ДДУ», что приводило
    /// к тому, что маппер создавал ДДУ с <c>Number="#N/A"</c>, при следующих строках
    /// находил тот же глобальный ДДУ → orphan-reanimate → Visary возвращал 500 (см. doc 101 v1.1).
    /// </summary>
    private static readonly HashSet<string> ExcelErrorMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "#N/A", "#REF!", "#VALUE!", "#NAME?", "#NUM!", "#DIV/0!", "#NULL!", "#GETTING_DATA",
    };

    /// <summary>
    /// Считает строку «пустой» с точки зрения файла: реально пустая или Excel-ошибка.
    /// Используется для столбцов, чьи значения уходят в Visary как-есть (НПС/Этап/ДДУ/PIN).
    /// </summary>
    private static bool IsBlankOrExcelError(string? s)
        => string.IsNullOrWhiteSpace(s) || ExcelErrorMarkers.Contains(s!.Trim());
    private static readonly string[] CostForOneAliases       = ["Стоимость кв,м/ руб,", "Стоимость кв.м", "CostForOne"];
    private static readonly string[] WholesaleRateAliases    = ["Скидка на опт.", "WholesaleRate"];
    private static readonly string[] MarketCostAliases       = ["Рыночная стоимость, руб.", "MarketCostPerM"];
    private static readonly string[] ZalogCostAliases        = ["Залоговая стоимость.", "ZalogCostPerM"];
    private static readonly string[] ShareAgreementAliases   = ["№ ДДУ", "ShareAgreementNumber"];

    // ── Дополнительные колонки (doc 113) — пишутся в Visary как есть,    ─
    // поиск перед CREATE/PATCH по ним не выполняется. Алиасы перечисляются ─
    // в обеих формах (с реальным \n, как кладёт ClosedXML для много-строчных ─
    // заголовков типа «Вывод\n(да/нет)», и без \n — на случай ручной правки ─
    // шаблона). ReadString сравнивает alias целиком, без нормализации, поэтому ─
    // каждую форму нужно перечислить явно. ──────────────────────────────────
    private static readonly string[] IsWithdrawnAliases          = [
        "Вывод\n(да/нет)", "Вывод (да/нет)", "Вывод", "IsWithdrawn"];
    private static readonly string[] SaCostAliases               = [
        "Стоимость ДКП, руб.", "Стоимость ДКП, руб,", "Стоимость ДКП, руб",
        "Сумма депонирования, руб.", "Сумма депонирования, руб",
        "Сумма депонирования",
        // Реальный шаблон заказчика объединяет оба лейбла одной ячейкой через
        // запятую-слэш (`, руб,/Сумма`). ReadString строго сравнивает alias
        // целиком — без явных «комбинированных» форм здесь поле молча
        // оставалось пустым и Visary не получал ShareAgreement.Cost. Slash-aware
        // fallback в ReadString также покрывает любые `A,/B` варианты.
        "Стоимость ДКП, руб,/Сумма депонирования, руб.",
        "Стоимость ДКП, руб./Сумма депонирования, руб.",
        "Стоимость ДКП, руб,/Сумма депонирования, руб",
        "Стоимость ДКП, руб./Сумма депонирования, руб",
        "Cost"];
    private static readonly string[] SaDepositedAmountAliases    = [
        "Сумма на эскроу", "DepositedAmount"];
    private static readonly string[] SaDateAliases               = [
        "Дата ДДУ", "Date"];
    private static readonly string[] SaDepositorFullNameAliases  = [
        "ФИО покупателя", "ФИО", "DepositorFullName"];

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

        // Project обязателен — пользователь выбирает только Проект; Site резолвится per-row
        // по (НПС, Этап) внутри проекта. См. doc_project/101-rooms-multi-site-by-project.md.
        if (context.VisaryProjectId is null)
        {
            fileErrors.Add(new RowError(null, "project_required",
                "Для импорта помещений необходимо выбрать Проект (объект строительства больше не выбирается — резолвится по строкам файла)."));
            return new ValidationResult([], fileErrors);
        }
        int projectId = context.VisaryProjectId.Value;
        if (context.VisarySiteId is not null)
        {
            _log.LogWarning(
                "RoomsForm.Validate: получен visarySiteId={SiteId} от UI, но импорт rooms резолвит Site per-row — значение игнорируется.",
                context.VisarySiteId.Value);
        }

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

        // ── Pre-pass: резолв Site per уникальной (НПС, Этап) ─────────────────
        // Собираем все уникальные пары из data-строк (служебные/сводные с пустыми
        // ключами отфильтруются естественным образом — их (НПС, Этап) обе пустые).
        // На каждую уникальную пару — один listview-запрос в проекте.
        var uniqueKeys = new HashSet<(string ProjectNum, string StageRaw)>();
        foreach (var pr in dataRows)
        {
            var pn = (ReadString(pr, ProjectNumberAliases) ?? string.Empty).Trim();
            var sn = (ReadString(pr, StageNumberAliases)   ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(pn) && string.IsNullOrEmpty(sn)) continue;
            uniqueKeys.Add((pn, sn));
        }

        var siteByKey = new Dictionary<(string ProjectNum, string StageRaw), SiteResolution>();
        foreach (var (pn, sn) in uniqueKeys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await _listView.GetSitesByProjectAndKeysAsync(projectId, pn, sn, ct);
                // Доп. локальная фильтрация: Visary "=" нечувствителен к whitespace,
                // но тип StageNumber в раз. сущностях смешанный — страхуемся Trim+OrdinalIgnoreCase.
                var matches = resp.Data
                    .Where(s => string.Equals((s.ConstructionProjectNumber ?? string.Empty).Trim(), pn,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(s => string.Equals((s.StageNumber ?? string.Empty).Trim(), sn,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                siteByKey[(pn, sn)] = new SiteResolution(matches);
                _log.LogInformation(
                    "RoomsForm.Validate: resolve site (project={ProjectId}, НПС='{P}', Этап='{S}') → matches={N} {IDs}",
                    projectId, pn, sn, matches.Count,
                    matches.Count == 0 ? "[]" : "[" + string.Join(",", matches.Select(m => m.ID)) + "]");
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "RoomsForm.Validate: resolve site failed (project={ProjectId}, НПС='{P}', Этап='{S}'): {Msg}",
                    projectId, pn, sn, ex.Message);
                siteByKey[(pn, sn)] = new SiteResolution(new List<ConstructionSiteRaw>(), ex.Message);
            }
        }

        var mappedRows = new List<MappedRow>(dataRows.Count);
        foreach (var row in dataRows)
        {
            ct.ThrowIfCancellationRequested();
            var rowErrors = new List<RowError>();

            // ── Ключи Site из строки ───────────────────────────────────────
            // Excel-ошибки («#N/A», «#REF!», …) обнуляем — пользователь оставил
            // битые формулы в источнике; не пытаемся резолвить/создавать по ним.
            var permission  = ReadString(row, PermissionNumberAliases);
            var projectNum  = ReadString(row, ProjectNumberAliases);
            var stageNumRaw = ReadString(row, StageNumberAliases);
            if (ExcelErrorMarkers.Contains(permission.Trim()))  permission  = string.Empty;
            if (ExcelErrorMarkers.Contains(projectNum.Trim()))  projectNum  = string.Empty;
            if (ExcelErrorMarkers.Contains(stageNumRaw.Trim())) stageNumRaw = string.Empty;

            // ── Тихий пропуск сводных/служебных строк ──────────────────────
            // Внутри листа «Квартира» (как в «Ежевика короткая 1.xlsx») сразу под
            // шапкой попадаются строки агрегатов: «ИТОГО», «Сумма с учетом вывода»,
            // «План», «Факт» — в первой колонке текст, остальные ячейки заполняются
            // формулами SUBTOTAL/SUMIF. Не считаем их данными: если ВСЕ три
            // идентификационных поля (НПС/РНС/Этап) пустые, строка не может
            // относиться к ОКС-у. Молча пропускаем — не порождая ошибки,
            // которыми иначе захлёбывается отчёт.
            if (string.IsNullOrWhiteSpace(permission)
                && string.IsNullOrWhiteSpace(projectNum)
                && string.IsNullOrWhiteSpace(stageNumRaw))
            {
                _log.LogDebug(
                    "RoomsForm.Validate: row {Row} (sheet '{Sheet}') пропущена — нет НПС/РНС/Этапа (служебная/сводная строка).",
                    row.SourceRowNumber, row.Sheet);
                continue;
            }

            // ── Per-row резолв Site внутри Project через кэш ───────────────
            var rowProjectNum = projectNum.Trim();
            int? rowStageNum  = ParseNullableInt(stageNumRaw);
            var rowStageRaw   = stageNumRaw.Trim();
            var rowPermission = permission.Trim();

            int? resolvedSiteId = null;
            string? resolvedSitePermission = null;
            if (string.IsNullOrEmpty(rowProjectNum) || string.IsNullOrEmpty(rowStageRaw))
            {
                rowErrors.Add(new RowError(null, "site_keys_missing",
                    $"для строки файла {row.SourceRowNumber} не заданы оба ключа: НПС='{rowProjectNum}', Этап='{rowStageRaw}'."));
            }
            else if (siteByKey.TryGetValue((rowProjectNum, rowStageRaw), out var reso))
            {
                if (reso.Matches.Count == 1)
                {
                    resolvedSiteId = reso.Matches[0].ID;
                    resolvedSitePermission = reso.Matches[0].ConstructionPermissionNumber;

                    // РНС больше не блокирует строку: раз Site уже однозначно резолвлен по (НПС,Этап),
                    // расхождение лишь информативно. PATCH РНС в Site (если в Site пусто, а в файле есть)
                    // отдан в Apply.TryUpdateSitePermissionNumberAsync — см. doc 101.
                    if (!string.IsNullOrEmpty(rowPermission)
                        && !string.IsNullOrEmpty(resolvedSitePermission)
                        && !string.Equals(rowPermission, resolvedSitePermission.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _log.LogDebug(
                            "RoomsForm.Validate: row {Row} — РНС из файла '{File}' расходится с Site '{Site}' (siteId={SiteId}). " +
                            "Не блокируем (site найден по НПС+Этап).",
                            row.SourceRowNumber, rowPermission, resolvedSitePermission, resolvedSiteId);
                    }
                }
                else if (reso.Matches.Count == 0)
                {
                    var msg = reso.Error is null
                        ? $"в проекте {projectId} не найден объект с НПС='{rowProjectNum}' и Этапом='{rowStageRaw}'."
                        : $"в проекте {projectId} не удалось получить список объектов: {reso.Error}";
                    rowErrors.Add(new RowError(null, "site_not_found_in_project", msg));
                }
                else
                {
                    var ids = string.Join(", ", reso.Matches.Select(m => m.ID));
                    rowErrors.Add(new RowError(null, "site_ambiguous",
                        $"в проекте {projectId} найдено несколько объектов с НПС='{rowProjectNum}' и Этапом='{rowStageRaw}' (ID: {ids}). " +
                        "Уточните данные в Visary."));
                }
            }
            else
            {
                // Ключи непустые, но pre-pass их не резолвил — теоретически невозможно.
                rowErrors.Add(new RowError(null, "site_resolve_unexpected",
                    $"внутренняя ошибка: пара (НПС='{rowProjectNum}', Этап='{rowStageRaw}') не была обработана в pre-pass."));
            }

            if (resolvedSiteId is null)
            {
                mappedRows.Add(new MappedRow(
                    row.SourceRowNumber,
                    row.Sheet ?? string.Empty,
                    IsValid: false,
                    JsonSerializer.SerializeToDocument(new { Sheet = row.Sheet }),
                    rowErrors));
                continue; // дальнейшая валидация без Site бессмысленна
            }

            // ── Поля поиска Room ────────────────────────────────────────────
            // Принимаем номер помещения как есть (включая текст и любые символы):
            // «п1», «12А», «ПХ-15», «Кладовка-А» сохраняются без нормализации.
            // См. doc 118 — заказчик использует не-числовые обозначения для нежилых.
            var roomNumberRaw = ReadString(row, RoomNumberAliases);
            var roomNumber = roomNumberRaw?.Trim() ?? string.Empty;
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
            // Excel-ошибки в этих полях — то же, что и пусто. Особенно критично для
            // `№ ДДУ`: пользователи Репино-Парк оставили колонку с формулой «#N/A»,
            // маппер пытался создавать SA с `Number="#N/A"` и потом «реанимировать»
            // тот же глобальный ДДУ id=809 для каждой следующей комнаты, Visary →
            // HTTP 500. См. doc 101 v1.1 (раздел про Excel-маркеры) и логи инцидента
            // «найден орфанный/несоответствующий ДДУ id=809 number='#N/A'».
            if (ExcelErrorMarkers.Contains(developerPin.Trim()))    developerPin    = string.Empty;
            if (ExcelErrorMarkers.Contains(shareAgreement.Trim()))  shareAgreement  = string.Empty;
            if (ExcelErrorMarkers.Contains(sectionTitle.Trim()))    sectionTitle    = string.Empty;
            if (ExcelErrorMarkers.Contains(floor.Trim()))           floor           = string.Empty;
            if (ExcelErrorMarkers.Contains(buildingSection.Trim())) buildingSection = string.Empty;

            // «Колич. комнат» нередко приходит в свободной форме: «1 к.», «1 к», «п1»,
            // «1п», «2-к», «3 ком.», «студия». Берём ПЕРВУЮ непрерывную группу цифр —
            // это и есть число комнат. Жёсткое int.TryParse тут не годится:
            // пользователю не должно прилетать invalid_number на «1 к.» — это
            // валидная однушка в реальных реестрах.
            //
            // Студии: маркеры «с»/«ст»/«студ»/«студия» (case-insensitive) ИЛИ числовой 0
            // означают студию — выставляем RoomsCount=0 и IsStudio=true. Для квартиры
            // в этом случае required_missing не выдаём (заказчик: «студия — валидный
            // вариант для Квартиры», см. doc 108).
            var roomsCountRaw = ReadString(row, RoomsCountAliases);
            int? roomsCount = ExtractFirstRunOfDigits(roomsCountRaw);
            bool isStudio = IsStudioMarker(roomsCountRaw) || roomsCount == 0;
            if (isStudio) roomsCount = 0;
            if (roomsCount.HasValue
                && !string.Equals(roomsCountRaw, roomsCount.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                _log.LogDebug(
                    "RoomsForm.Validate: row {Row} — «Колич. комнат» '{Raw}' нормализовано в {N}{Studio}.",
                    row.SourceRowNumber, roomsCountRaw, roomsCount.Value,
                    isStudio ? " (студия)" : string.Empty);
            }

            // Если вид помещения «Квартира» — «Количество комнат» обязательно
            // (студия считается заданным значением, RoomsCount=0 + IsStudio=true).
            if (roomsCount is null && !isStudio
                && !string.IsNullOrWhiteSpace(roomKindTitle)
                && string.Equals(roomKindTitle.Trim(), "Квартира", StringComparison.OrdinalIgnoreCase))
            {
                rowErrors.Add(new RowError(string.Join(" / ", RoomsCountAliases), "required_missing",
                    "Не указано количество комнат для квартиры."));
            }

            double? projectArea = TryParseNullableDouble(ReadString(row, ProjectAreaAliases), out var paErr);
            if (paErr != null) rowErrors.Add(new RowError(string.Join(" / ", ProjectAreaAliases), "invalid_number", paErr));

            // Отдельная «Общая площадь, кв.м.» — для нежилых (машиноместо/кладовая/нежилое).
            // Для жилых она обычно не заполняется (площадь идёт в ProjectArea).
            double? totalArea = TryParseNullableDouble(ReadString(row, TotalAreaAliases), out var taErr);
            if (taErr != null) rowErrors.Add(new RowError(string.Join(" / ", TotalAreaAliases), "invalid_number", taErr));

            double? costForOne = TryParseNullableDouble(ReadString(row, CostForOneAliases), out var cErr);
            if (cErr != null) rowErrors.Add(new RowError(string.Join(" / ", CostForOneAliases), "invalid_number", cErr));

            double? wholesale  = TryParseNullableDouble(ReadString(row, WholesaleRateAliases), out var wErr);
            if (wErr != null) rowErrors.Add(new RowError(string.Join(" / ", WholesaleRateAliases), "invalid_number", wErr));

            double? marketCost = TryParseNullableDouble(ReadString(row, MarketCostAliases), out var mErr);
            if (mErr != null) rowErrors.Add(new RowError(string.Join(" / ", MarketCostAliases), "invalid_number", mErr));

            double? zalogCost  = TryParseNullableDouble(ReadString(row, ZalogCostAliases), out var zErr);
            if (zErr != null) rowErrors.Add(new RowError(string.Join(" / ", ZalogCostAliases), "invalid_number", zErr));

            // ── Дополнительные поля Помещения/ДДУ (doc 113) ─────────────────
            // Поиск перед CREATE/PATCH по ним НЕ выполняется — пишем в Visary
            // как есть. Все поля опциональные: пустая ячейка → null → не уйдёт
            // в payload (`WhenWritingNull`-семантика DTO).
            bool? isWithdrawn = TryParseBoolYesNo(ReadString(row, IsWithdrawnAliases));

            double? saCost = TryParseNullableDouble(ReadString(row, SaCostAliases), out var saCostErr);
            if (saCostErr != null) rowErrors.Add(new RowError(string.Join(" / ", SaCostAliases), "invalid_number", saCostErr));

            double? saDeposited = TryParseNullableDouble(ReadString(row, SaDepositedAmountAliases), out var saDepErr);
            if (saDepErr != null) rowErrors.Add(new RowError(string.Join(" / ", SaDepositedAmountAliases), "invalid_number", saDepErr));

            string? saDate = TryParseExcelDate(ReadString(row, SaDateAliases), out var saDateErr);
            if (saDateErr != null) rowErrors.Add(new RowError(string.Join(" / ", SaDateAliases), "invalid_date", saDateErr));

            var saDepositorFullName = ReadString(row, SaDepositorFullNameAliases);
            if (ExcelErrorMarkers.Contains(saDepositorFullName.Trim())) saDepositorFullName = string.Empty;

            // ПИН застройщика уже прочитан выше как `developerPin` (используется
            // в developer-link flow). Кладём его же в SA.DeveloperPIN — заказчик
            // просил прокинуть значение без дополнительных проверок.
            var saDeveloperPin = developerPin;

            // Категория Kind (residential/non-residential) — нужна Apply, чтобы
            // решить, в какое поле положить площадь.
            int? roomCategory = (kindId != 0 && categoryByKindId.TryGetValue(kindId, out var cat))
                ? cat
                : null;

            var mapped = new Dictionary<string, object?>
            {
                ["Sheet"]                = row.Sheet,
                // SiteId резолвлен в pre-pass из (НПС, Этап). Apply группирует
                // строки по SiteId — каждая группа получает свой snapshot/sections.
                ["SiteId"]               = resolvedSiteId,
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
                ["IsStudio"]             = isStudio,
                ["ProjectArea"]          = projectArea,
                ["TotalArea"]            = totalArea,
                ["CostForOne"]           = costForOne,
                ["WholesaleRate"]        = wholesale,
                ["MarketCostPerM"]       = marketCost,
                ["ZalogCostPerM"]        = zalogCost,
                ["ShareAgreementNumber"] = shareAgreement,
                // doc 113 — дополнительные поля Помещения/ДДУ.
                ["IsWithdrawn"]                  = isWithdrawn,
                ["ShareAgreementCost"]           = saCost,
                ["ShareAgreementDepositedAmount"] = saDeposited,
                ["ShareAgreementDate"]           = saDate,
                ["ShareAgreementDepositorFullName"] = saDepositorFullName,
                ["ShareAgreementDeveloperPin"]   = saDeveloperPin,
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

        if (context.VisaryProjectId is null)
        {
            errors.Add(new RowError(null, "project_required",
                "Не указан проект (visaryProjectId)."));
            return new ApplyResult(0, errors.ToList());
        }
        int? projectId = context.VisaryProjectId;

        var validRows = rows.Where(mr => mr.IsValid).ToList();

        // ── ⓪ Группировка валидных строк по SiteId (резолвлен в Validate) ───
        // Раньше Site был один на сессию; теперь файл может содержать несколько
        // ОКС в рамках проекта. Все pre-pass'ы (snapshot/РНС/секции/developer)
        // выполняются sequential по сайтам — внутри сайта flow прежний.
        var rowsBySite = validRows
            .GroupBy(mr => GetIntOrNull(mr.MappedValues.RootElement, "SiteId") ?? 0)
            .Where(g => g.Key > 0)
            .ToDictionary(g => g.Key, g => g.ToList());
        _log.LogInformation(
            "RoomsForm.Apply: projectId={ProjectId}, validRows={Count}, sites={Sites} [{Ids}]",
            projectId, validRows.Count, rowsBySite.Count,
            string.Join(",", rowsBySite.Keys));

        // ── ① Pre-load snapshots для всех задействованных сайтов ────────────
        var snapshotsByKey = new ConcurrentDictionary<RoomSnapshotKey, RoomApplySnapshot>();
        using (var loadScope = _scopeFactory.CreateScope())
        {
            var store = loadScope.ServiceProvider.GetRequiredService<RoomApplySnapshotStore>();
            foreach (var sid in rowsBySite.Keys)
            {
                var perSite = await store.LoadForSiteAsync(sid, ct);
                foreach (var kv in perSite) snapshotsByKey[kv.Key] = kv.Value;
            }
        }
        _log.LogInformation(
            "RoomsForm.Apply: snapshotsPreloaded={Snap}", snapshotsByKey.Count);

        // ── ② Pre-pass per-site: РНС в Site + Sections + Developer link ─────
        // Sequential по сайтам — это N короткоживущих pre-pass'ов; параллелизм
        // оставляем на основной цикл (по группам внутри всех сайтов).
        var sectionCache = new ConcurrentDictionary<(int SiteId, string Title), int>();
        foreach (var (sid, siteRows) in rowsBySite)
        {
            await TryUpdateSitePermissionNumberAsync(sid, siteRows, ct);

            var sectionTitlesNeeded = siteRows
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
                var existing = await _listView.GetSectionsBySiteAsync(sid, sectionTitle, ct);
                var sectionTitleTrim = sectionTitle.Trim();
                var match = existing.Data.FirstOrDefault(x =>
                    string.Equals((x.Title ?? string.Empty).Trim(), sectionTitleTrim,
                        StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    sectionCache[(sid, sectionTitle)] = match.ID;
                }
                else
                {
                    _log.LogInformation(
                        "RoomsForm.Apply: корпус не найден — создаём (siteId={SiteId}, title='{Title}')",
                        sid, sectionTitle);
                    var created = await _crud.CreateSectionAsync(new SectionCreateRequest
                    {
                        ConstructionSiteID = sid,
                        ConstructionSite   = new VisaryRef { ID = sid },
                        Title              = sectionTitle,
                        Type               = new VisaryRef { ID = 3, Title = "МЖД" },
                    }, ct);
                    sectionCache[(sid, sectionTitle)] = created.ID;
                }
            }

            // Developer link per-site sequential (внутри метод тоже sequential).
            projectId = await ResolveDeveloperLinksAsync(sid, projectId, siteRows, Log, ct);
        }

        // ── ⑤ Main: Parallel.ForEachAsync по группам (SiteId, Sheet, Section) ─
        // Группа = (siteId, sheet, section). Внутри группы строки sequential —
        // защита Room.find-or-create от дублей при одинаковом (Kind, RoomNumber,
        // BuildingSection) в нескольких строках одной секции одного сайта.
        var groupsByKey = validRows
            .GroupBy(mr =>
            {
                var v = mr.MappedValues.RootElement;
                var sid     = GetIntOrNull(v, "SiteId") ?? 0;
                var sheet   = GetStringOrNull(v, "Sheet") ?? "<unknown>";
                var section = GetStringOrNull(v, "SectionTitleNumeric")
                              ?? GetStringOrNull(v, "SectionTitle") ?? string.Empty;
                return (SiteId: sid, Sheet: sheet, Section: section);
            })
            .Where(g => g.Key.SiteId > 0) // на всякий случай: SiteId=0 — Validate провалился
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
            var siteId = group.Key.SiteId;
            var sheetForRow = group.Key.Sheet;
            var sectionTitle = string.IsNullOrWhiteSpace(group.Key.Section) ? null : group.Key.Section;
            int? sectionId = sectionTitle is not null
                && sectionCache.TryGetValue((siteId, sectionTitle), out var sid)
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

            // Локальный кэш ДДУ по roomId внутри группы. Используется:
            //   ① snapshot-revalidation: при hash-match нужно убедиться, что ДДУ
            //      из snapshot всё ещё существует в Visary (иначе помещение/ДДУ
            //      могли удалить → snapshot устарел, надо пересоздать);
            //   ② основной flow ДДУ (find-or-create) — переиспользуем тот же лист.
            // Это даёт максимум 1 GetShareAgreementsByRoomAsync на (roomId) даже
            // если строка прошла через revalidation и потом через normal flow.
            var saByRoomCache = new Dictionary<int, List<ShareAgreementRaw>>();

            foreach (var mr in group)
            {
                gct.ThrowIfCancellationRequested();
                var v = mr.MappedValues.RootElement;
                try
                {
                    var roomNumber = GetStringOrNull(v, "RoomNumber") ?? string.Empty;
                    var kindId = GetIntOrNull(v, "RoomKindId");
                    var buildingSection = GetStringOrNull(v, "BuildingSection") ?? string.Empty;

                    // ── (a) Diff-hash → skip-кандидат ────────────────────────
                    // Сравниваем хэш текущего MappedValues с тем, что лежит в snapshot.
                    // Hash-match — НЕОБХОДИМОЕ, но не ДОСТАТОЧНОЕ условие для skip:
                    // помещение/ДДУ могли удалить в Visary, оставив наш snapshot устаревшим.
                    // Поэтому ниже делаем revalidation против реального состояния Visary.
                    var snapKey = RoomApplySnapshotStore.BuildKey(
                        siteId, sheetForRow, sectionTitle ?? string.Empty,
                        kindId, roomNumber, buildingSection);
                    var hash = RoomApplySnapshotStore.ComputeMappedHash(v);

                    if (snapshotsByKey.TryGetValue(snapKey, out var prev)
                        && string.Equals(prev.MappedHash, hash, StringComparison.Ordinal))
                    {
                        // ── (a') Revalidation snapshot против реального Visary ─
                        // Зачем: пользователь мог удалить Room/ДДУ в Visary между
                        // импортами. Если skip-нём только по hash, помещение
                        // не восстановится. Проверяем существование per-row.
                        var (revalidated, staleReason) = await RevalidateSnapshotAsync(
                            prev, roomsInSection, saByRoomCache, gct);

                        if (revalidated)
                        {
                            // Snapshot жив — помещение/ДДУ существуют, hash совпал.
                            // Это и есть инкрементальный импорт: пропускаем PATCH-и.
                            Log(sheetForRow, mr.SourceRowNumber, "Без изменений — пропуск (snapshot)");
                            Interlocked.Increment(ref skipped);
                            Interlocked.Increment(ref applied);
                            continue;
                        }

                        // Snapshot устарел — продолжаем обычный flow (Room/SA find-or-create),
                        // он либо переиспользует существующую сущность, либо создаст новую.
                        // Запись в snapshot будет перезаписана с актуальными VisaryRoomId/SaId.
                        Log(sheetForRow, mr.SourceRowNumber,
                            $"Snapshot устарел ({staleReason}) — пересоздаём");
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

                    // Раскладка площади:
                    //   • Жилые (Residential): ProjectArea ← «Площадь (для квартир …, кв.м.)».
                    //   • Нежилые (NonResidential/ParkingPlace/OtherNonResidential):
                    //     TotalArea ← «Общая площадь, кв.м.» (если задана), иначе
                    //     fallback на ProjectArea — раньше для машиноместа в Visary
                    //     приходило только `"ProjectArea":0` и `TotalArea` оставался
                    //     пустым (см. инцидент Репино-Парк, doc 101 v1.1).
                    var projectAreaFile = GetDoubleOrNull(v, "ProjectArea");
                    var totalAreaFile   = GetDoubleOrNull(v, "TotalArea");
                    var roomCategory = GetIntOrNull(v, "RoomCategory");
                    var isNonResidential = roomCategory.HasValue && roomCategory.Value != ResidentialRoomCategory;
                    double? projectAreaForCrud = isNonResidential ? 0d : projectAreaFile;
                    double? totalAreaForCrud   = isNonResidential
                        ? (totalAreaFile ?? projectAreaFile)
                        : null;

                    // doc 113 diagnostics: явно логируем парсенные значения новых полей
                    // на каждой строке. Помогает выявить случаи, когда «Признак вывода»
                    // в Visary остаётся пустым: видно, дошло ли значение до payload,
                    // или MappedValues уже null (header не совпал / TryParseBoolYesNo не
                    // распознал значение / snapshot diff-skip).
                    var diagIsWithdrawn  = GetBoolOrNull(v, "IsWithdrawn");
                    var diagSaCost       = GetDoubleOrNull(v, "ShareAgreementCost");
                    var diagSaDate       = GetStringOrNull(v, "ShareAgreementDate");
                    var diagSaDeposited  = GetDoubleOrNull(v, "ShareAgreementDepositedAmount");
                    var diagSaDepositor  = GetStringOrNull(v, "ShareAgreementDepositorFullName");
                    _log.LogInformation(
                        "RoomsForm.Apply.Doc113 sheet='{Sheet}' row={Row} roomNumber='{RoomNumber}' "
                        + "IsWithdrawn={IsWithdrawn} ShareAgreementCost={Cost} "
                        + "ShareAgreementDate={Date} ShareAgreementDepositedAmount={Deposited} "
                        + "ShareAgreementDepositorFullName='{Depositor}'",
                        sheetForRow, mr.SourceRowNumber, roomNumber,
                        diagIsWithdrawn?.ToString() ?? "null",
                        diagSaCost?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
                        diagSaDate ?? "null",
                        diagSaDeposited?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
                        diagSaDepositor ?? "null");

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
                            IsStudio          = GetBoolOrNull(v, "IsStudio"),
                            IsWithdrawn       = GetBoolOrNull(v, "IsWithdrawn"),
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
                            IsStudio        = GetBoolOrNull(v, "IsStudio"),
                            IsWithdrawn     = GetBoolOrNull(v, "IsWithdrawn"),
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
                        // doc 119: ДДУ с таким же бизнес-ключом, но УЖЕ привязан к
                        // другому помещению (Room.ID > 0 && != roomId). Не «угоняем»
                        // его — создаём новый ДДУ для текущего помещения и логируем
                        // что отверженный кандидат существовал.
                        ShareAgreementRaw? saRejectedOwnedByOtherRoom = null;

                        try
                        {
                            // Если revalidation уже подняла список ДДУ для этого Room —
                            // переиспользуем кэш, чтобы не дёргать Visary дважды на одну строку.
                            List<ShareAgreementRaw> saList;
                            if (saByRoomCache.TryGetValue(roomId.Value, out var cached))
                            {
                                saList = cached;
                            }
                            else
                            {
                                var byRoom = await _listView.GetShareAgreementsByRoomAsync(roomId.Value, null, gct);
                                saList = byRoom.Data.ToList();
                                saByRoomCache[roomId.Value] = saList;
                            }
                            // doc 120: симметричный safeguard. `onetomany/Room?associationId={roomId}`
                            // по контракту должно возвращать только ДДУ, привязанные к roomId, но
                            // в проде встречались случаи, когда Visary отдавал в этом списке ДДУ,
                            // фактически принадлежащий другому помещению (Room.ID > 0 && != roomId).
                            // Без фильтра по Room.ID он попадал в matched-in-room ветку и PATCH-ил
                            // его на наш roomId — то самое «угоняние», от которого защищает doc 119
                            // в strict/loose. Здесь — то же правило.
                            var byRoomCandidates = saList
                                .Where(a => string.Equals(
                                    (a.Number ?? string.Empty).Trim(), saNumberTrim,
                                    StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            saMatch = byRoomCandidates
                                .Where(a => a.Room is null
                                            || a.Room.ID <= 0
                                            || a.Room.ID == roomId.Value)
                                .OrderByDescending(a => a.ID)
                                .FirstOrDefault();
                            if (saMatch is not null) matchedInRoom = true;
                            else
                            {
                                saRejectedOwnedByOtherRoom = byRoomCandidates
                                    .Where(a => a.Room is not null
                                                && a.Room.ID > 0
                                                && a.Room.ID != roomId.Value)
                                    .FirstOrDefault();
                            }
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
                                // Шаг А — строгий поиск по полному бизнес-ключу (5 полей, doc 76).
                                // Находит ДДУ, у которых Project+Stage УЖЕ совпадают с текущей
                                // строкой (классический дедуп). Безопасен от «угона» ДДУ из
                                // соседнего проекта/этапа.
                                var foundStrict = await _listView.FindShareAgreementsAsync(
                                    number:            saNumber,
                                    roomKindId:        kindId,
                                    conditionalNumber: roomNumber,
                                    stageNumber:       stageNumberForSa,
                                    projectNumber:     projectNumberForSa,
                                    gct);

                                var strictCandidates = foundStrict.Data
                                    .Where(a => string.Equals(
                                        (a.Number ?? string.Empty).Trim(), saNumberTrim,
                                        StringComparison.OrdinalIgnoreCase))
                                    .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
                                    .OrderByDescending(a => a.ID)
                                    .ToList();

                                // doc 119: принимаем только orphan-ДДУ (Room null/<=0)
                                // или уже привязанные к нашему roomId. Кандидата,
                                // принадлежащего другому помещению, не «угоняем» —
                                // запоминаем для лога и идём в CREATE-ветку.
                                saMatch = strictCandidates
                                    .Where(a => a.Room is null
                                                || a.Room.ID <= 0
                                                || a.Room.ID == roomId.Value)
                                    .FirstOrDefault();

                                if (saMatch is null && saRejectedOwnedByOtherRoom is null)
                                {
                                    // doc 120: не перезаписываем «отвергнутого» из
                                    // matched-in-room ветки — он сохраняет ID реального
                                    // владельца ДДУ для журнала.
                                    saRejectedOwnedByOtherRoom = strictCandidates
                                        .Where(a => a.Room is not null
                                                    && a.Room.ID > 0
                                                    && a.Room.ID != roomId.Value)
                                        .FirstOrDefault();
                                }

                                // Шаг Б — loose-поиск без Stage/Project (doc 76 v1.1).
                                // Orphan-ДДУ (вручную или системно отвязанные от Room/Project/Stage)
                                // не находятся строгим фильтром, потому что Visary `=` на NULL/пустой
                                // StageNumber/ProjectNumber их отсекает. Без этой ступени каждый
                                // импорт плодит дубликат ДДУ рядом с orphan-ом, который так и остаётся
                                // невидимым. Безопасность: принимаем строки, где Room **не указывает на
                                // реальное помещение** — `Room == null` ИЛИ `Room.ID <= 0` (Visary часто
                                // сериализует «нет связи» как `{"ID":0,"Title":""}` вместо JSON-null;
                                // VisaryRef.ID — non-nullable int → 0 по умолчанию). НЕ трогаем ДДУ,
                                // легитимно принадлежащую другому проекту/этапу (anti-pattern #2 в doc 76).
                                if (saMatch is null)
                                {
                                    var foundLoose = await _listView.FindShareAgreementsAsync(
                                        number:            saNumber,
                                        roomKindId:        kindId,
                                        conditionalNumber: roomNumber,
                                        stageNumber:       null,
                                        projectNumber:     null,
                                        gct);

                                    var candidates = foundLoose.Data
                                        .Where(a => string.Equals(
                                            (a.Number ?? string.Empty).Trim(), saNumberTrim,
                                            StringComparison.OrdinalIgnoreCase))
                                        .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
                                        .ToList();

                                    // Диагностика: видим в логе, какие ДДУ Visary вернул и почему мы
                                    // считаем их (не) orphan-ами. Без этого в проде нельзя отличить
                                    // «loose ничего не вернул» от «вернул, но всё non-orphan».
                                    if (candidates.Count > 0)
                                    {
                                        _log.LogInformation(
                                            "RoomsForm.Apply: loose-find SA '{Num}' (Cond='{Cond}', Kind={Kind}) " +
                                            "вернул {Total}: {Brief}",
                                            saNumber, roomNumber, kindId, candidates.Count,
                                            string.Join("; ", candidates.Take(5).Select(a =>
                                                $"id={a.ID}/Room.ID={a.Room?.ID.ToString() ?? "null"}")));
                                    }

                                    saMatch = candidates
                                        .Where(a => a.Room is null
                                                    || a.Room.ID <= 0
                                                    || a.Room.ID == roomId.Value)
                                        .OrderByDescending(a => a.ID)
                                        .FirstOrDefault();

                                    // doc 119: запоминаем «не наш» для лога CREATE-ветки.
                                    if (saMatch is null && saRejectedOwnedByOtherRoom is null)
                                    {
                                        saRejectedOwnedByOtherRoom = candidates
                                            .Where(a => a.Room is not null
                                                        && a.Room.ID > 0
                                                        && a.Room.ID != roomId.Value)
                                            .FirstOrDefault();
                                    }
                                }
                            }
                            catch (Exception findEx)
                            {
                                _log.LogWarning(findEx,
                                    "RoomsForm.Apply: глобальный поиск ДДУ '{Number}' не удался: {Msg} — будет создан новый.",
                                    saNumber, findEx.Message);
                            }
                        }

                        // doc 120: финальная защита от «угона». Источник правды о привязке
                        // ДДУ — `GET /crud/shareagreement/{id}` (ShareAgreementFull.Room).
                        // Listview-ответ (`shareagreement` / `shareagreementall`) в проде
                        // встречался с `Room=null`/`{ID:0}` для ДДУ, который на самом деле
                        // уже привязан к другому помещению. orphan-фильтр в strict/loose
                        // в этом случае ошибочно принимал «orphan» — и мы PATCH-или его
                        // на наш roomId. CRUD GET даёт авторитативный Room, проверяем.
                        // matched-in-room не верифицируем: связь там уже подтверждена
                        // запросом по associationId={roomId}.
                        if (saMatch is not null && !matchedInRoom)
                        {
                            var saMatchToVerify = saMatch;
                            try
                            {
                                var saFull = await _crud.GetShareAgreementByIdAsync(saMatchToVerify.ID, gct);
                                if (saFull?.Room is not null
                                    && saFull.Room.ID > 0
                                    && saFull.Room.ID != roomId.Value)
                                {
                                    _log.LogWarning(
                                        "RoomsForm.Apply: ДДУ id={SaId} number='{Num}' в listview шёл как orphan (Room null/0), " +
                                        "CRUD GET показывает Room.ID={OtherRoom} — отвергаем reuse, создаём новый для roomId={NewRoom}.",
                                        saMatch.ID, saNumber, saFull.Room.ID, roomId.Value);
                                    saRejectedOwnedByOtherRoom ??= new ShareAgreementRaw
                                    {
                                        ID     = saFull.ID,
                                        Number = saFull.Number,
                                        Room   = saFull.Room,
                                    };
                                    saMatch = null;
                                }
                            }
                            catch (Exception verifyEx)
                            {
                                _log.LogWarning(verifyEx,
                                    "RoomsForm.Apply: CRUD-верификация ДДУ id={SaId} не удалась: {Msg} — продолжаем по listview-данным.",
                                    saMatchToVerify.ID, verifyEx.Message);
                            }
                        }

                        // Дополнительные поля ДДУ (doc 113) — пишем как есть,
                        // без поиска перед CREATE/PATCH. Берём из MappedValues
                        // строки (заполняется в Validate из колонок XLSX).
                        var saExtraCost           = GetDoubleOrNull(v, "ShareAgreementCost");
                        var saExtraDeposited      = GetDoubleOrNull(v, "ShareAgreementDepositedAmount");
                        var saExtraDate           = GetStringOrNull(v, "ShareAgreementDate");
                        var saExtraDepositor      = GetStringOrNull(v, "ShareAgreementDepositorFullName");
                        var saExtraDeveloperPin   = GetStringOrNull(v, "ShareAgreementDeveloperPin");

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
                                Cost              = saExtraCost,
                                DepositedAmount   = saExtraDeposited,
                                Date              = saExtraDate,
                                DepositorFullName = string.IsNullOrWhiteSpace(saExtraDepositor) ? null : saExtraDepositor,
                                DeveloperPIN      = string.IsNullOrWhiteSpace(saExtraDeveloperPin) ? null : saExtraDeveloperPin,
                            }, gct);
                            saId = saCreated.ID;
                            if (saRejectedOwnedByOtherRoom is not null)
                            {
                                // doc 119: явно проговариваем в журнале, что глобально
                                // найден ДДУ с тем же номером, но он уже привязан к
                                // другому помещению — мы его не трогаем, а создаём новый.
                                _log.LogInformation(
                                    "RoomsForm.Apply: ДДУ '{Number}' уже привязан к Room.ID={OtherRoom} (saId={OtherSa}) — создан новый saId={NewSa} для roomId={NewRoom}",
                                    saNumber, saRejectedOwnedByOtherRoom.Room?.ID, saRejectedOwnedByOtherRoom.ID,
                                    saCreated.ID, roomId.Value);
                                Log(sheetForRow, mr.SourceRowNumber,
                                    $"ДДУ создан (№{saNumber}); существующий ДДУ id={saRejectedOwnedByOtherRoom.ID} оставлен у Room.ID={saRejectedOwnedByOtherRoom.Room?.ID}");
                            }
                            else
                            {
                                Log(sheetForRow, mr.SourceRowNumber, $"ДДУ создан (№{saNumber})");
                            }
                        }
                        else
                        {
                            saId = saMatch.ID;
                            var isOrphan = saMatch.Room?.ID is null || saMatch.Room.ID != roomId.Value;
                            if (matchedInRoom)
                                Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден в помещении (не создан, №{saNumber})");
                            else if (isOrphan)
                            {
                                // doc 119: сюда теперь попадают только orphan'ы
                                // (Room null/<=0) — после фильтра в strict/loose
                                // ДДУ другого помещения как saMatch не приходит.
                                _log.LogInformation(
                                    "RoomsForm.Apply: orphan-ДДУ id={SaId} number='{Num}' (Room={ExistingRoom}) — привязываем к roomId={NewRoom}",
                                    saMatch.ID, saNumber, saMatch.Room?.ID, roomId.Value);
                                Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден глобально как orphan (привязан к помещению, №{saNumber})");
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
                                Cost              = saExtraCost,
                                DepositedAmount   = saExtraDeposited,
                                Date              = saExtraDate,
                                DepositorFullName = string.IsNullOrWhiteSpace(saExtraDepositor) ? null : saExtraDepositor,
                                DeveloperPIN      = string.IsNullOrWhiteSpace(saExtraDeveloperPin) ? null : saExtraDeveloperPin,
                            }, gct);
                        }
                    }

                    // ── (c.1) Финальный PATCH помещения (doc 113 workaround) ──
                    // Visary `POST /crud/room` ТИХО ДРОПАЕТ поле `IsWithdrawn` —
                    // в payload CREATE отправляем `true`, но GET потом возвращает
                    // `false` (дефолт). Подтверждено логами: POST body с
                    // `"IsWithdrawn":true` → GET /room/24899 → `"IsWithdrawn":false`.
                    // Видимо, CREATE-эндпоинт принимает ограниченный набор полей;
                    // PATCH (`forceUpdate=true`) принимает корректно.
                    //
                    // Кроме того, привязка/создание ДДУ выше может на стороне Visary
                    // пересчитать поля Room (ActiveShareAgreement и т.п.) — финальный
                    // PATCH ПОСЛЕ блока SA гарантирует, что наше значение
                    // `IsWithdrawn` зафиксировано в актуальном состоянии.
                    //
                    // Шлём только если из файла пришло non-null значение (пользователь
                    // явно указал «да»/«нет»). Пусто → не трогаем, Visary оставит
                    // дефолт. Накладные расходы — 1 PATCH/строку при наличии данных.
                    if (roomId is int finalRoomId && diagIsWithdrawn is bool isWithdrawnVal)
                    {
                        await _crud.PatchRoomAsync(finalRoomId, new RoomPatchRequest
                        {
                            IsWithdrawn = isWithdrawnVal,
                        }, gct);
                        Log(sheetForRow, mr.SourceRowNumber,
                            $"Помещение: IsWithdrawn={isWithdrawnVal} применён через follow-up PATCH после привязки ДДУ");
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
                    // doc 113 v1.3 / doc 100-pattern: привязываем ошибку к
                    // (Sheet, SourceRowNumber), чтобы фронт показал её под
                    // нужной строкой листа, а не в file-level блоке без
                    // понятного контекста. Текст префиксом «row N» оставлен
                    // для совместимости с UI-фильтром и логами.
                    errors.Add(new RowError(null, "apply_failed",
                        $"row {mr.SourceRowNumber}: {ex.Message}",
                        SourceRowNumber: mr.SourceRowNumber,
                        Sheet: sheetForRow));
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
    /// Snapshot-revalidation против реального состояния Visary. Hash MappedValues
    /// уже совпал с <paramref name="prev"/> — но это говорит только про входные
    /// данные импорта, не про живость сущностей в Visary. Пользователь мог удалить
    /// помещение/ДДУ между импортами; без этой проверки skip-by-hash «маскировал»
    /// бы удалённые сущности — повторный импорт того же файла не восстановил бы их.
    ///
    /// Проверяем два уровня:
    /// <list type="number">
    ///   <item><description><b>Room.</b> Ищем <c>prev.VisaryRoomId</c> в уже-загруженном
    ///     <c>roomsInSection</c> (стоимость 0 — listview/room уже сделан в начале группы).
    ///     Нет → snapshot устарел, выходим без проверки ДДУ.</description></item>
    ///   <item><description><b>ShareAgreement.</b> Если в snapshot был <c>VisaryShareAgreementId</c>,
    ///     грузим ДДУ по комнате (один <c>GetShareAgreementsByRoomAsync</c> на roomId,
    ///     кэшируется в <paramref name="saByRoomCache"/> и переиспользуется
    ///     основным flow). Нет — snapshot устарел.</description></item>
    /// </list>
    ///
    /// На сетевой ошибке проверки ДДУ — возвращаем «живо» (true), чтобы временные
    /// сбои не запускали полный пересчёт всей сессии.
    /// </summary>
    /// <returns>
    /// <c>(true, null)</c> — snapshot валиден (можно skip-нуть строку);
    /// <c>(false, причина)</c> — устарел, продолжать обычный flow.
    /// </returns>
    private async Task<(bool Live, string? StaleReason)> RevalidateSnapshotAsync(
        RoomApplySnapshot prev,
        IReadOnlyList<RoomRaw> roomsInSection,
        Dictionary<int, List<ShareAgreementRaw>> saByRoomCache,
        CancellationToken ct)
    {
        // ── 1. Room-existence ────────────────────────────────────────────────
        if (prev.VisaryRoomId is int prevRoomId)
        {
            var roomExists = roomsInSection.Any(r => r.ID == prevRoomId);
            if (!roomExists)
            {
                _log.LogInformation(
                    "RoomsForm.Apply.Revalidate: помещение roomId={RoomId} (snapshotId={SnapId}) " +
                    "не найдено в секции — snapshot устарел.",
                    prevRoomId, prev.Id);
                return (false, $"помещение №{prev.RoomNumber} удалено в Visary");
            }
        }

        // ── 2. ShareAgreement-existence ──────────────────────────────────────
        // Если в snapshot не было ДДУ — нечего проверять (строка без ДДУ
        // или прошлый импорт обработал её без SA).
        if (prev.VisaryShareAgreementId is int prevSaId && prev.VisaryRoomId is int rid)
        {
            try
            {
                if (!saByRoomCache.TryGetValue(rid, out var saList))
                {
                    var byRoom = await _listView.GetShareAgreementsByRoomAsync(rid, null, ct);
                    saList = byRoom.Data.ToList();
                    saByRoomCache[rid] = saList;
                }
                var saExists = saList.Any(a => a.ID == prevSaId);
                if (!saExists)
                {
                    _log.LogInformation(
                        "RoomsForm.Apply.Revalidate: ДДУ saId={SaId} (number='{Num}') не найден " +
                        "в комнате roomId={RoomId} — snapshot устарел.",
                        prevSaId, prev.ShareAgreementNumber, rid);
                    return (false, $"ДДУ №{prev.ShareAgreementNumber ?? "?"} удалён в Visary");
                }
            }
            catch (Exception saCheckEx)
            {
                // Сетевая ошибка на проверке — НЕ инвалидируем snapshot, иначе
                // временный сбой Visary запустит full-rewrite всей сессии.
                // Прежнее поведение skip-а сохраняется при недоступности проверки.
                _log.LogWarning(saCheckEx,
                    "RoomsForm.Apply.Revalidate: проверка ДДУ saId={SaId} roomId={RoomId} " +
                    "не удалась: {Msg} — считаем snapshot валидным.",
                    prevSaId, rid, saCheckEx.Message);
            }
        }

        return (true, null);
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
    /// Резолвит имя листа («Квартиры», «Машиноместа», «Кладовые»,
    /// «Коммерческие помещения», «Нежилое помещение», …) в Title/ID из
    /// справочника RoomKind. Стратегии (по порядку):
    ///   1) точное совпадение `kindByTitle[sheetName]`;
    ///   2) plural-trim КАЖДОГО слова независимо: имя листа разбивается
    ///      по пробелам, для каждого слова собираются ед.ч.-кандидаты,
    ///      перебирается декартово произведение, склеенный кандидат
    ///      ищется в справочнике.
    /// Возвращает (null, null) если ничего не подошло. Substring-fallback
    /// сознательно не используется (см. doc 90): иначе «Кв_01.04.26»
    /// совпало бы с «Квартира».
    /// </summary>
    internal static (int? Id, string? Title) ResolveKindBySheetName(
        string sheetName, IDictionary<string, int> kindByTitle)
    {
        if (string.IsNullOrWhiteSpace(sheetName)) return (null, null);
        var name = sheetName.Trim();

        // 1. Прямое совпадение (case-insensitive благодаря StringComparer.OrdinalIgnoreCase в kindByTitle)
        if (kindByTitle.TryGetValue(name, out var id1))
            return (id1, FindMatchingTitle(name, kindByTitle));

        // 2. Plural-trim per-word + декартово произведение.
        //    Однословное имя — обычная plural-эвристика.
        //    Многословное (например, «Коммерческие помещения») — каждое слово
        //    приводится к ед.ч. независимо: «Коммерческие»→«Коммерческое»,
        //    «помещения»→«помещение»; склейка ищется в справочнике.
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var perWord = words.Select(w => SingularCandidates(w).Distinct().ToList()).ToList();
        // Защита от комбинаторного взрыва. Реальные имена — до 3 слов × ~4 кандидата = 64.
        long total = 1;
        foreach (var s in perWord) total *= s.Count;
        if (total > 512) return (null, null);

        foreach (var combo in CartesianProduct(perWord))
        {
            var candidate = string.Join(' ', combo);
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (kindByTitle.TryGetValue(candidate, out var idN))
                return (idN, FindMatchingTitle(candidate, kindByTitle));
        }
        return (null, null);
    }

    /// <summary>
    /// Для слова в множественном числе возвращает потенциальные формы
    /// единственного числа. Эвристика «срез последней буквы» + типичные
    /// замены русских окончаний (мн.ч.→ед.ч.):
    /// <list type="bullet">
    ///   <item><c>«ые» → «ая»</c> («Кладовые» → «Кладовая», «Жилые» → «Жилая»)</item>
    ///   <item><c>«ие» → «ое»</c> («Коммерческие» → «Коммерческое»)</item>
    ///   <item><c>«ия» → «ие»</c> («помещения» → «помещение»)</item>
    ///   <item><c>«ы» → «а»</c>, <c>«и» → «я»</c>, <c>«а» → «о»</c> (старая логика)</item>
    /// </list>
    /// Кандидаты, не совпавшие ни с одним Title в справочнике, безопасно
    /// отбрасываются — главное не пропустить корректный матч.
    /// </summary>
    private static IEnumerable<string> SingularCandidates(string word)
    {
        if (string.IsNullOrEmpty(word)) { yield return word; yield break; }
        yield return word; // уже singular либо direct-match
        if (word.Length < 2) yield break;

        var head1 = word[..^1];
        var head2 = word.Length >= 2 ? word[..^2] : string.Empty;
        var last1 = word[^1];
        var last2 = word.Length >= 2 ? word[^2..] : string.Empty;

        // 2-буквенные суффиксы — берём сначала, чтобы «Кладовые» → «Кладовая»,
        // а не остановиться на ложном candidate «Кладовы».
        if (string.Equals(last2, "ые", StringComparison.OrdinalIgnoreCase)) yield return head2 + "ая";
        if (string.Equals(last2, "ие", StringComparison.OrdinalIgnoreCase)) yield return head2 + "ое";
        if (string.Equals(last2, "ия", StringComparison.OrdinalIgnoreCase)) yield return head2 + "ие";

        // Однобуквенные plural-эвристики (старая логика).
        if ("аяыиеёАЯЫИЕЁ".IndexOf(last1) >= 0) yield return head1;
        if (last1 == 'ы' || last1 == 'Ы') yield return head1 + "а";
        if (last1 == 'и' || last1 == 'И') yield return head1 + "я";
        if (last1 == 'а' || last1 == 'А') yield return head1 + "о";
    }

    private static IEnumerable<IEnumerable<string>> CartesianProduct(IList<List<string>> sets)
    {
        IEnumerable<IEnumerable<string>> result = new[] { Enumerable.Empty<string>() };
        foreach (var s in sets)
        {
            var snapshot = s;
            result = result.SelectMany(prefix => snapshot.Select(item => prefix.Append(item)));
        }
        return result;
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

    /// <summary>
    /// Текстовые маркеры студии в колонке «Колич. комнат»: «с», «ст», «студ»,
    /// «студия» (case-insensitive, после Trim). Сравнение точное по полному
    /// слову — иначе «секция»/«склад» ложно сматчатся. Числовой 0 студией
    /// здесь не считается (отдельная проверка в Validate).
    /// </summary>
    internal static bool IsStudioMarker(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        return string.Equals(s, "с",      StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "ст",     StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "студ",   StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "студия", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Парсит значение колонки «Вывод (да/нет)» (doc 113) в <see cref="bool"/>:
    /// <list type="bullet">
    ///   <item><description><c>true</c>  → «да», «yes», «y», «true», «1», «+», «✓»</description></item>
    ///   <item><description><c>false</c> → «нет», «no», «n», «false», «0», «-», «—»</description></item>
    ///   <item><description><c>null</c>  → пусто/whitespace/неизвестное значение</description></item>
    /// </list>
    /// Сравнение case-insensitive после Trim. На незнакомом значении возвращаем
    /// <c>null</c> (не ошибку): по требованию заказчика поле опциональное и не
    /// блокирующее — в Visary просто не уйдёт.
    /// </summary>
    internal static bool? TryParseBoolYesNo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (string.Equals(s, "да",   StringComparison.OrdinalIgnoreCase)
         || string.Equals(s, "yes",  StringComparison.OrdinalIgnoreCase)
         || string.Equals(s, "y",    StringComparison.OrdinalIgnoreCase)
         || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
         || s == "1" || s == "+" || s == "✓") return true;
        if (string.Equals(s, "нет",  StringComparison.OrdinalIgnoreCase)
         || string.Equals(s, "no",   StringComparison.OrdinalIgnoreCase)
         || string.Equals(s, "n",    StringComparison.OrdinalIgnoreCase)
         || string.Equals(s, "false",StringComparison.OrdinalIgnoreCase)
         || s == "0" || s == "-" || s == "—") return false;
        return null;
    }

    /// <summary>
    /// Парсит дату из ячейки XLSX в ISO-формат <c>yyyy-MM-dd</c> для Visary
    /// (см. doc 113 v1.4). Реальный payload Visary UI (`POST /crud/shareagreement`)
    /// шлёт `"Date":"2026-05-26"` строкой — числовой Excel-serial не принимается.
    /// ClosedXML возвращает либо отформатированную строку (для cell-format = Date),
    /// либо «голый» Excel-serial (для General). Распознаём оба варианта:
    /// <list type="number">
    ///   <item><description>Число в диапазоне <c>[1, 80000]</c> — Excel-serial,
    ///     конвертируется через <see cref="DateTime.FromOADate(double)"/> →
    ///     <c>yyyy-MM-dd</c>. Диапазон закрывает реальные даты ДДУ (~1900..2118).</description></item>
    ///   <item><description>Текстовая дата в форматах <c>dd.MM.yyyy</c>,
    ///     <c>yyyy-MM-dd</c>, <c>dd/MM/yyyy</c>, <c>MM/dd/yyyy</c> (опц. с
    ///     <c>HH:mm:ss</c>) — <see cref="DateTime.TryParseExact"/> →
    ///     <c>yyyy-MM-dd</c>.</description></item>
    /// </list>
    /// Возвращает <c>null</c> на пустой строке (прочерк/«—» — тоже null без
    /// ошибки) и записывает причину в <paramref name="error"/> для row-error.
    /// </summary>
    internal static string? TryParseExcelDate(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s == "-" || s == "—") return null;

        // 1) Excel-serial (число) — конвертируем в `yyyy-MM-dd` через FromOADate.
        // Диапазон [1; 80000] закрывает реальные даты ДДУ (~1900..2118) и
        // исключает случайные суммы из других колонок.
        var normalized = s.Replace(',', '.').Replace(" ", string.Empty);
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial >= 1 && serial <= 80000)
        {
            return DateTime.FromOADate(serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // 2) Текстовые форматы (русский dd.MM.yyyy + ISO + слэши, опц. время).
        // ClosedXML возвращает разные строковые формы в зависимости от формата
        // ячейки и локали шаблона. Поддерживаем:
        //   • dd.MM.yyyy / d.M.yyyy            (русский, точки)
        //   • yyyy-MM-dd                       (ISO)
        //   • dd/MM/yyyy / d/M/yyyy            (русский, слэши — `04/07/2025` → Jul 4)
        //   • MM/dd/yyyy / M/d/yyyy            (US — `11/27/2025` → Nov 27, fallback)
        // Все варианты — с опциональным `HH:mm:ss` и `H:mm:ss` (без leading zero).
        // ВАЖНО: dd/MM* стоит ДО MM/dd* — для неоднозначных строк (оба ≤12,
        // напр. `04/07/2025`) сохраняется русская семантика.
        string[] formats =
        {
            "dd.MM.yyyy", "d.M.yyyy",
            "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ",
            "dd/MM/yyyy", "d/M/yyyy",
            "MM/dd/yyyy", "M/d/yyyy",
            "dd.MM.yyyy HH:mm:ss", "d.M.yyyy HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "dd/MM/yyyy HH:mm:ss", "d/M/yyyy HH:mm:ss",
            "dd/MM/yyyy H:mm:ss",  "d/M/yyyy H:mm:ss",
            "MM/dd/yyyy HH:mm:ss", "M/d/yyyy HH:mm:ss",
            "MM/dd/yyyy H:mm:ss",  "M/d/yyyy H:mm:ss",
            "dd.MM.yyyy H:mm:ss",  "d.M.yyyy H:mm:ss",
            "yyyy-MM-dd H:mm:ss",
        };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        error = $"'{s}' не является валидной датой (поддерживаются Excel-serial и форматы dd.MM.yyyy / yyyy-MM-dd / dd/MM/yyyy / MM/dd/yyyy, опционально с HH:mm:ss).";
        return null;
    }

    /// <summary>«Лит 1.1» → «1.1»; «корп 2» → «2»; «3.А» → «3»; «лит. 1» → «1»;
    /// «литер 1-1» → «1-1»; «лит 1/1» → «1/1»; «лит 1\1» → «1\1».</summary>
    internal static string? ExtractNumericPart(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '-' || ch == '/' || ch == '\\')
                sb.Append(ch == ',' ? '.' : ch);
            else if (sb.Length > 0 && ch != ' ')
                break;
        }
        return sb.Length == 0 ? null : sb.ToString().Trim('.');
    }

    private static string ReadString(ParsedRow row, string[] aliases)
    {
        // 1) Быстрый путь — exact match по ключу (для алиасов вроде `IsWithdrawn`).
        foreach (var key in aliases)
        {
            if (row.Cells.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        // 2) Whitespace-insensitive fallback. ClosedXML возвращает многострочные
        // заголовки с реальным `\n` / `\r\n` / `\t` / двойными пробелами; alias
        // в коде может содержать одиночный `\n` либо одиночный пробел. `Trim()`
        // нормализует только края — нужна полная collapse-нормализация, иначе
        // doc 113 поля (Вывод/Стоимость/Сумма на эскроу/Дата/ФИО) теряются
        // из-за рассинхрона форм заголовка.
        foreach (var key in aliases)
        {
            var keyNorm = NormalizeHeader(key);
            var match = row.Cells.FirstOrDefault(p =>
                string.Equals(NormalizeHeader(p.Key), keyNorm, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key) && !string.IsNullOrWhiteSpace(match.Value))
                return match.Value.Trim();
        }
        // 3) Slash-aware fallback. Шаблоны заказчика часто объединяют два
        // альтернативных лейбла в одной ячейке через `,/` (русская конвенция):
        // «Стоимость ДКП, руб,/Сумма депонирования, руб.». Без явного
        // перечисления комбинаций ReadString их не ловит и поле молча
        // остаётся пустым (Visary не получает ShareAgreement.Cost).
        //
        // Сегментируем ТОЛЬКО по `,/` (запятая-слэш) — не по голому `/`,
        // иначе «Вывод (да/нет)» разорвалось бы на «Вывод (да» / «нет)»
        // и сломались бы другие алиасы со слэшем внутри парных меток.
        var separator = new[] { ",/" };
        foreach (var key in aliases)
        {
            var keyNorm = NormalizeHeader(key);
            foreach (var (cellKey, cellValue) in row.Cells)
            {
                if (string.IsNullOrWhiteSpace(cellValue)) continue;
                if (!cellKey.Contains(",/", StringComparison.Ordinal)) continue;
                var segments = cellKey.Split(separator, StringSplitOptions.None);
                foreach (var seg in segments)
                {
                    if (string.Equals(NormalizeHeader(seg), keyNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        return cellValue.Trim();
                    }
                }
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Сворачивает любую последовательность whitespace (`\n`, `\r`, `\t`,
    /// неразрывный пробел, NBSP, многократные пробелы) в один пробел и
    /// тримит края. Используется в <see cref="ReadString"/>: реальный
    /// заголовок «Вывод\n(да/нет)» матчится с alias «Вывод (да/нет)» и
    /// наоборот.
    /// </summary>
    private static string NormalizeHeader(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        var prevWasSpace = false;
        foreach (var ch in s)
        {
            //   — NBSP, тоже попадает из Excel.
            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                if (!prevWasSpace && sb.Length > 0) sb.Append(' ');
                prevWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevWasSpace = false;
            }
        }
        // Trailing space, если последний был whitespace.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
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

    private static bool? GetBoolOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}
