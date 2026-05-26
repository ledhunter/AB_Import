using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using KiloImportService.Api.Budget;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;
using KiloImportService.Api.Domain.Mapping.Budget;
using KiloImportService.Api.Domain.Pipeline;
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
///   • «Тип отделки»          → FinishingMaterial (FK на Site)
///   • «Класс жилья»          → EstateClass       (FK на Site, в Visary «Класс недвижимости»)
///   • «Строительный адрес»   → Address           (строковый атрибут Site)
///   • «Площадь застройки»    → ConstructionSiteIndicator + ConstructionSiteIndicatorValue
///                               с конкретной стадией (Stage = 50 «Экспертиза»)
///   • «ИНН» + «Заемщик/Застройщик» (раздел «Основные данные») → Organization (поиск по
///     ClientID=ИНН, при отсутствии — POST /crud/organization) + projectmanagement-запись
///     (Заемщик/Застройщик) в проекте объекта. Пара колонок опциональна — старые шаблоны
///     без раздела «Основные данные» продолжают работать. См. doc 99.
///   • Бюджет («Себестоимость») → WBS (ИСР), главы и подстатьи. Title из файла резолвится
///     в Code (КБК) через эталонный справочник <see cref="IBudgetReferenceProvider"/>.
///     Идемпотентно: на повторном импорте суммы у существующих подстатей PATCH-аются,
///     дубликаты не создаются.
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
    ///     «Выбрать количество этапов»;
    ///   • в той же «Inputs» ниже маркера «Себестоимость» лежит секция бюджета,
    ///     которую парсер эмитит отдельным набором строк (Sheet с суффиксом «(budget)»).
    /// </summary>
    public FileLayoutHint LayoutHint { get; } = new KeyValueVertical(
        SheetName: "Inputs",
        KeyColumn: "C",
        ValueStartColumn: "H",
        StageCount: new StageCountReference(
            SheetName: "Control",
            KeyColumn: "F",
            ValueColumn: "G",
            ParameterName: "Выбрать количество этапов"),
        Budget: new BudgetSectionHint(
            MarkerColumn: "C",
            StartMarker: "Себестоимость",
            // Секция бюджета заканчивается перед блоком исторической отчётности
            // (или его эквивалентом). Любой из этих текстов в C → стоп.
            EndMarkers: new[]
            {
                "Историческая фин. отчетность",
                "Бухгалтерский баланс",
                "Финансовые показатели",
            },
            LastIncludedColumn: "G"),
        // ГФ Главы 1 — горизонтальный «квартальный» блок. Шапка с датами начала кварталов
        // в строке 7, квартальные суммы — в колонках H..CU (23 квартала, далее идут годовые).
        // Маппер берёт только Этап 1 и матчит статьи в коды 1.1/1.6/1.8 через
        // BudgetReferenceProvider; всё остальное — пропускает. См. doc_project/91-finmodel-chapter1-schedule.md.
        ChapterSchedule: new ChapterScheduleHint(
            MarkerColumn: "C",
            StartMarker: "Глава 1.",
            EndMarker: "Глава 2.",
            QuarterHeaderRow: 7,
            FirstQuarterColumn: "H",
            LastQuarterColumn: "CU"),
        // «Группа компаний» — единственный параметр, значение которого лежит не
        // в колонке этапа (H/I/...), а в фиксированной E14. C14 — текст-ключ
        // («Группа компаний»). Override-механизм парсера подставляет E14 в
        // Cells["Группа компаний"] для каждого ParsedRow всех этапов. См. doc 100.
        SingleValues: new[]
        {
            new SingleValueOverride(KeyText: "Группа компаний", ValueColumn: "E"),
        },
        // «Номер КД» лежит не на Inputs, а на управляющем листе Control в той же
        // (F=key, G=value)-раскладке, что и «Выбрать количество этапов» (см. doc 104
        // v1.3). Парсер находит строку по тексту «Номер КД» в колонке F и подставляет
        // значение из колонки G как Cells["Номер договора"] во все эмитируемые
        // ParsedRow — чтобы маппер мог читать его тем же ReadCellTrimmed-кодом, что
        // и обычные параметры Inputs.
        ControlValues: new[]
        {
            new ControlValueRef(
                SheetName: "Control",
                KeyColumn: "F",
                ValueColumn: "G",
                ParameterName: "Номер КД",
                OutputKey: "Номер договора"),
        });

    private static readonly string[] FinishingTypeAliases =
        ["Тип отделки", "FinishingType", "Finishing"];

    // «Класс жилья» в шаблоне = «Класс недвижимости» (EstateClass) на стороне Visary.
    private static readonly string[] EstateClassAliases =
        ["Класс жилья", "EstateClass", "Класс недвижимости"];

    // «Строительный адрес» — простой строковый атрибут Site (поле Address в Visary).
    // Не справочник, поэтому без TryLoadDictionaryAsync / ResolveDictionaryValue.
    private static readonly string[] AddressAliases =
        ["Строительный адрес", "Address", "Адрес"];

    // Раздел «Основные данные»: ИНН организации-застройщика/заёмщика. Используется
    // для поиска Organization в Visary по ClientID (поле ClientID=ИНН в Visary).
    // Колонки опциональные: если пара ИНН + Title отсутствует — Apply пропускает
    // organization-link flow без ошибки. Если найдена только одна из двух —
    // row-error «value_empty» на отсутствующее значение.
    private static readonly string[] InnAliases =
        ["ИНН", "INN", "ИНН организации", "ИНН Застройщика", "ИНН Заемщика", "ИНН Заёмщика"];

    // Наименование организации-застройщика/заёмщика. В шаблоне «Параметры к переносу в АБ.xlsx»
    // строка C17 содержит ровно «Заемщик/Застройщик» (через слэш, буква «е», не «ё»).
    private static readonly string[] BorrowerOrganizationAliases =
        [
            "Заемщик/Застройщик", "Заёмщик/Застройщик",
            "Заемщик / Застройщик", "Заёмщик / Застройщик",
            "Застройщик/Заемщик", "Застройщик/Заёмщик",
            "Застройщик", "Заемщик", "Заёмщик",
            "Borrower", "Developer", "BorrowerTitle",
        ];

    // «Группа компаний» — наименование материнской ГК для организации-застройщика.
    // Значение лежит в E14 (см. SingleValues override в LayoutHint выше), а текст-
    // ключ в C14 совпадает с одним из этих алиасов. Колонка опциональна: шаблоны
    // без раздела «Основные данные» / без этой строки продолжают работать.
    private static readonly string[] CompanyGroupAliases =
        ["Группа компаний", "ГК", "CompanyGroup", "Group"];

    // «Номер договора» — pre-check на наличие сделки (Deal) в выбранном проекте перед
    // любыми изменениями Объекта (см. doc 104). С v1.3 значение приходит с управляющего
    // листа «Control», поле «Номер КД» — парсер кладёт его в Cells["Номер договора"]
    // каждой строки через ControlValueRef. LmID **не используется** во flow:
    // фильтр Visary listview/deal и payload CreateDealAsync идут только по DocNumber
    // (по запросу заказчика 2026-05-21 v1.3). Если/когда понадобится вернуть LmID —
    // достаточно добавить алиасы и колонку обратно; код фильтра/payload готов через
    // опциональные параметры IListViewClient.GetDeals* и DealCreateRequest.LmID.
    private static readonly string[] DocNumberAliases =
        ["Номер договора", "№ договора", "Номер Договора", "DocNumber", "Doc Number"];

    // Domain.Model.Enums.ProjectStage: 50 = Expertise (Экспертиза).
    // Источник: FinModel/Альфа Банк. Управление проектами.drawio.xml — диаграмма enum'а.
    private const int ProjectStageExpertise = 50;
    private const string ExpertiseHumanName = "Экспертиза";

    // Маркер «строки бюджета», эмитируемой XlsxParser-ом (см. BudgetSectionHint.SheetMarker).
    // У бюджетных ParsedRow — Sheet вида "Inputs (budget)". Все остальные строки идут
    // через обычный flow (KV-параметры/показатели).
    private const string BudgetSheetSuffix = "(budget)";

    // Маркер «строки ГФ», эмитируемой XlsxParser-ом (см. ChapterScheduleHint.SheetMarker).
    // У schedule-строк — Sheet вида "Inputs (schedule)".
    private const string ScheduleSheetSuffix = "(schedule)";

    // Колонки квартального блока ГФ Главы 1 в файле «Параметры к переносу в АБ.xlsx»:
    // H = 8-я колонка (первый квартал), CU = 99-я (23-й квартал). За CU идут годовые
    // колонки CV..DS — в v1 их игнорируем (см. п.3 решения от пользователя).
    private const string ScheduleFirstQuarterColumn = "H";
    private const string ScheduleLastQuarterColumn = "CU";

    // Алиасы Title → Code Главы 1 для случаев, когда BudgetReferenceProvider не справляется:
    // в файле «Параметры к переносу в АБ.xlsx» статья «Прочие затраты» короче справочного
    // «Прочие затраты на улучшения и содержание ЗУ» (1.8) — reverse-prefix провайдера
    // не сработает (он fuzzy только когда file-title ДЛИННЕЕ ref-title). Закрепляем явным
    // alias'ом. Пользователь подтвердил соответствие (решение от 2026-05-19, п.2).
    private static readonly IReadOnlyDictionary<string, string> Chapter1TitleAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Прочие затраты"] = "1.8.",
        };

    // Маркеры разделов внутри Главы 1: только Этап 1 (по решению пользователя от 2026-05-19, п.1).
    // Этап 2/3 в файле содержат те же три статьи (повтор), их пропускаем.
    private const string Chapter1Stage1Marker = "Этап 1";
    private const string Chapter1StageMarkerPrefix = "Этап";
    private const string Chapter1TotalMarkerPrefix = "Итого";

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
    private readonly IBudgetReferenceProvider _budgetRef;
    // BudgetVisaryUploader зарегистрирован Scoped (зависит от ImportServiceDbContext),
    // а мапер — Singleton (общий регистр стратегий). Поэтому загружать его напрямую
    // нельзя (captive dependency) — каждый раз открываем мини-scope через factory.
    private readonly IServiceScopeFactory _scopeFactory;
    // IFileStorage — Singleton (LocalFileStorage без scoped-зависимостей), поэтому
    // инжектируется напрямую. Используется для чтения второго (опционального) файла
    // импорта — листа «План» FinModel, откуда берутся краевые квартальные значения
    // для создания fmmodel. См. doc 110.
    private readonly IFileStorage _fileStorage;

    public FinModelImportMapper(
        ILogger<FinModelImportMapper> log,
        ICrudClient visaryClient,
        IListViewClient listViewClient,
        IBudgetReferenceProvider budgetRef,
        IServiceScopeFactory scopeFactory,
        IFileStorage fileStorage)
    {
        _log = log;
        _visaryClient = visaryClient;
        _listViewClient = listViewClient;
        _budgetRef = budgetRef;
        _scopeFactory = scopeFactory;
        _fileStorage = fileStorage;
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

        // Разделяем строки по типу секции:
        //   • (budget)   — табличная секция бюджета (главы/статьи + Итого);
        //   • (schedule) — квартальный блок ГФ Главы 1 (dates-header + статьи Этапа 1);
        //   • остальное  — обычные KV-стадии (тип отделки, класс, показатели).
        // Источник суффиксов — BudgetSectionHint.SheetMarker / ChapterScheduleHint.SheetMarker.
        var budgetRows = rows.Where(IsBudgetRow).ToList();
        var scheduleRows = rows.Where(IsScheduleRow).ToList();
        var stageRows = rows
            .Where(r => !IsBudgetRow(r) && !IsScheduleRow(r))
            .ToList();

        var (paramMappedRows, paramFileErrors) = await ValidateParametersAsync(
            stageRows, visaryDb, ct);
        fileErrors.AddRange(paramFileErrors);

        var budgetMappedRows = ValidateBudget(budgetRows, fileErrors);
        var scheduleMappedRows = ValidateChapter1Schedule(scheduleRows, fileErrors);

        // Если параметрический поток отбраковал всё (нет целевых колонок шаблона) и
        // бюджет+ГФ тоже пусты — возвращаем только file-level errors. Если хоть один
        // поток дал mapped-строки — возвращаем их вместе.
        var combined = new List<MappedRow>(
            paramMappedRows.Count + budgetMappedRows.Count + scheduleMappedRows.Count);
        combined.AddRange(paramMappedRows);
        combined.AddRange(budgetMappedRows);
        combined.AddRange(scheduleMappedRows);

        _log.LogInformation(
            "FinModelImportMapper.ValidateAsync: completed paramRows={Param} budgetRows={Budget} scheduleRows={Schedule} fileErrors={FileErrorCount}",
            paramMappedRows.Count, budgetMappedRows.Count, scheduleMappedRows.Count, fileErrors.Count);
        return new ValidationResult(combined, fileErrors);
    }

    private async Task<(List<MappedRow> Rows, List<RowError> FileErrors)> ValidateParametersAsync(
        IReadOnlyList<ParsedRow> rows, VisaryDbContext visaryDb, CancellationToken ct)
    {
        var fileErrors = new List<RowError>();
        var mappedRows = new List<MappedRow>();

        if (rows.Count == 0)
            return (mappedRows, fileErrors);

        // Тянем оба справочника один раз на сессию.
        var finishingByTitle = await TryLoadDictionaryAsync(
            "Тип отделки",
            ct => _listViewClient.ListFinishingMaterialsAsync(ct),
            m => m.ID, m => m.Title,
            fileErrors, ct);
        if (finishingByTitle is null)
            return (mappedRows, fileErrors);

        var estateByTitle = await TryLoadDictionaryAsync(
            "Класс недвижимости",
            ct => _listViewClient.ListEstateClassesAsync(ct),
            m => m.ID, m => m.Title,
            fileErrors, ct);
        if (estateByTitle is null)
            return (mappedRows, fileErrors);

        // Pre-flight колонок.
        var allColumns = rows
            .SelectMany(r => r.Cells.Keys)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileFinishingCol = FindColumn(allColumns, FinishingTypeAliases);
        var fileEstateCol    = FindColumn(allColumns, EstateClassAliases);
        var fileAddressCol   = FindColumn(allColumns, AddressAliases);
        // Раздел «Основные данные»: ИНН + Заемщик/Застройщик — опциональная пара.
        // Если одна из колонок есть, а другой нет — это ошибка строки (см. ниже),
        // но file-level error «column_not_found» не выдаём (поля не обязательны для
        // обратной совместимости с шаблонами без раздела «Основные данные»).
        var fileInnCol       = FindColumn(allColumns, InnAliases);
        var fileBorrowerCol  = FindColumn(allColumns, BorrowerOrganizationAliases);
        // «Группа компаний» — независимая опциональная колонка (без пары). Если её нет —
        // ГК-flow пропускается; если есть и значение пустое — column присутствует, но
        // ReadCellTrimmed вернёт null и LinkCompanyGroup пропустится.
        var fileCompanyGroupCol = FindColumn(allColumns, CompanyGroupAliases);
        // «Номер договора» — опциональная одиночная колонка. С v1.3 значение приходит
        // с управляющего листа Control (поле «Номер КД» в F-колонке), парсер
        // подставляет его как Cells["Номер договора"] во все ParsedRow. Pre-check Deal
        // в проекте делается в ApplyAsync; здесь только читаем значение.
        var fileDocNumberCol = FindColumn(allColumns, DocNumberAliases);
        var indicatorCols    = Indicators
            .Select(p => (Param: p, Col: FindColumn(allColumns, p.Aliases)))
            .ToArray();

        // Если НИ ОДНОЙ целевой колонки нет — пользователь явно загрузил не тот шаблон.
        var anyFound = fileFinishingCol is not null
                       || fileEstateCol is not null
                       || fileAddressCol is not null
                       || fileInnCol is not null
                       || fileBorrowerCol is not null
                       || fileCompanyGroupCol is not null
                       || fileDocNumberCol is not null
                       || indicatorCols.Any(x => x.Col is not null);

        if (!anyFound)
        {
            var allAliases = FinishingTypeAliases
                .Concat(EstateClassAliases)
                .Concat(AddressAliases)
                .Concat(InnAliases)
                .Concat(BorrowerOrganizationAliases)
                .Concat(CompanyGroupAliases)
                .Concat(DocNumberAliases)
                .Concat(Indicators.SelectMany(p => p.Aliases))
                .ToArray();
            fileErrors.Add(BuildColumnNotFoundError(allColumns, allAliases,
                "Не найдены целевые колонки шаблона 'Финмодель'"));
            _log.LogWarning("FinModelImportMapper.ValidateAsync: no target columns found. Detected: {Detected}",
                string.Join(", ", allColumns));
            return (mappedRows, fileErrors);
        }

        // Какая-то колонка нашлась, но не все — отдельная file-level ошибка на каждую.
        if (fileFinishingCol is null)
            fileErrors.Add(BuildColumnNotFoundError(allColumns, FinishingTypeAliases,
                "Не найдена колонка 'Тип отделки'"));
        if (fileEstateCol is null)
            fileErrors.Add(BuildColumnNotFoundError(allColumns, EstateClassAliases,
                "Не найдена колонка 'Класс жилья'"));
        if (fileAddressCol is null)
            fileErrors.Add(BuildColumnNotFoundError(allColumns, AddressAliases,
                "Не найдена колонка 'Строительный адрес'"));
        foreach (var (param, col) in indicatorCols)
        {
            if (col is null)
                fileErrors.Add(BuildColumnNotFoundError(allColumns, param.Aliases,
                    $"Не найдена колонка '{param.HumanName}'"));
        }
        if (fileErrors.Count > 0)
            return (mappedRows, fileErrors);

        var allowedFinishing = string.Join(", ", finishingByTitle.Values.Select(v => v.Title));
        var allowedEstate    = string.Join(", ", estateByTitle.Values.Select(v => v.Title));

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

            var addressValue = ReadCellTrimmed(
                row, fileAddressCol!, AddressAliases, "Строительный адрес", rowErrors);

            // Раздел «Основные данные»: ИНН + Заемщик/Застройщик. Колонки опциональны
            // как пара. Поведение:
            //   • обе колонки найдены → читаем оба значения (value_empty при пустоте);
            //   • найдена только одна → value_empty на отсутствующее значение (требуем
            //     согласованную пару, иначе непонятно как создавать Organization);
            //   • ни одной → Apply пропустит organization-link без ошибки.
            string? innValue = null;
            string? borrowerTitleValue = null;
            if (fileInnCol is not null || fileBorrowerCol is not null)
            {
                innValue = ReadCellTrimmed(
                    row, fileInnCol ?? "ИНН", InnAliases, "ИНН", rowErrors);
                borrowerTitleValue = ReadCellTrimmed(
                    row,
                    fileBorrowerCol ?? "Заемщик/Застройщик",
                    BorrowerOrganizationAliases,
                    "Заемщик/Застройщик",
                    rowErrors);
            }

            // «Группа компаний»: НЕ требуем непустого значения (опциональный признак,
            // привязка идёт только если в файле явно указано наименование). Поэтому
            // читаем без rowErrors-add — TryReadCellTrimmed бы тоже работал, но в коде
            // нет такого хелпера; используем GetTrimmedCellValue, который возвращает
            // null/пусто без ошибок.
            string? companyGroupValue = null;
            if (fileCompanyGroupCol is not null
                && row.Cells.TryGetValue(fileCompanyGroupCol, out var cgRaw)
                && !string.IsNullOrWhiteSpace(cgRaw))
            {
                companyGroupValue = cgRaw.Trim();
            }

            // «Номер договора» — опциональная одиночная колонка (с v1.3 — из листа
            // Control). Если колонки нет — pre-check Deal пропускается; если есть и
            // значение пустое — value_empty. LmID больше не используется (см. doc 104 v1.3).
            string? docNumberValue = null;
            if (fileDocNumberCol is not null)
            {
                docNumberValue = ReadCellTrimmed(
                    row, fileDocNumberCol,
                    DocNumberAliases, "Номер договора", rowErrors);
            }

            var indicatorValues = new Dictionary<string, double>();
            foreach (var (param, col) in indicatorCols)
            {
                var v = ResolveDoubleValue(row, col!, param.Aliases, param.HumanName, rowErrors);
                if (v.HasValue) indicatorValues[param.HumanName] = v.Value;
            }

            if (rowErrors.Count > 0)
            {
                mappedRows.Add(new MappedRow(row.SourceRowNumber, row.Sheet ?? string.Empty, false, JsonDocument.Parse("{}"), rowErrors));
                continue;
            }

            var mappedJson = JsonSerializer.Serialize(new
            {
                Kind                   = "params",
                FinishingMaterialId    = finishingEntry!.Value.Id,
                FinishingMaterialTitle = finishingEntry.Value.Title,
                EstateClassId          = estateEntry!.Value.Id,
                EstateClassTitle       = estateEntry.Value.Title,
                Address                = addressValue,
                Inn                    = innValue,
                BorrowerTitle          = borrowerTitleValue,
                CompanyGroupTitle      = companyGroupValue,
                DocNumber              = docNumberValue,
                Indicators             = indicatorValues,
            });

            mappedRows.Add(new MappedRow(
                row.SourceRowNumber, row.Sheet ?? string.Empty, true, JsonDocument.Parse(mappedJson), rowErrors));
        }

        return (mappedRows, fileErrors);
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

        // Финмодель (fmmodel) — ортогонально mapped-строкам: создаётся из
        // ОТДЕЛЬНОГО файла («План»), которого может вовсе не быть в основном.
        // Поэтому вызов ДО проверки validRows.Count==0: даже если в Inputs пусто
        // (или вообще не валидно), но есть второй файл с планами — Финмодель
        // надо создать. См. doc 110.
        var siteIdForFmModel = context.VisarySiteId.Value;
        if (context.VisaryProjectId is { } projectIdForFmModel)
        {
            await EnsureFmModelAsync(
                projectIdForFmModel, siteIdForFmModel,
                context.SecondaryFileRelativePath, errors, ct);
        }

        var validRows = rows.Where(r => r.IsValid).ToList();
        if (validRows.Count == 0)
        {
            _log.LogWarning("Нет валидных строк для применения.");
            return new ApplyResult(0, errors);
        }

        // Разделяем mapped-строки по Kind.
        var paramRows = validRows.Where(r => GetKind(r) == "params").ToList();
        var budgetRows = validRows.Where(r => GetKind(r) == "budget").ToList();
        var scheduleArticleRows = validRows.Where(r => GetKind(r) == "schedule_article").ToList();
        var scheduleQuartersRow = validRows.FirstOrDefault(r => GetKind(r) == "schedule_quarters");

        var siteId = context.VisarySiteId.Value;
        int applied = 0;
        var rowActions = new List<RowActionLog>();

        bool paramsApplied = false;
        if (paramRows.Count > 0)
        {
            // Pre-check: до любых записей в Объекте проверяем, что в выбранном проекте
            // есть Visary Deal с таким же (LmID, DocNumber). Колонки опциональны: если
            // в файле их нет — чек skip-ается (return true). При несовпадении —
            // row-error «deal_not_found» на каждой param-строке (фронт привяжет к
            // ячейкам), ApplyParametersAsync пропускается. Бюджет и ГФ продолжаются
            // самостоятельно — они работают на уровне Project/WBS, не Site. См. doc 104.
            var dealOk = await EnsureDealExistsInProjectAsync(
                siteId, paramRows, visaryDb, errors, rowActions, ct);
            if (dealOk)
            {
                var paramApply = await ApplyParametersAsync(siteId, paramRows, errors, ct);
                applied += paramApply;
                paramsApplied = paramApply > 0;
            }
        }

        // budget upload status — управляет тем, можно ли запускать ГФ Главы 1.
        // null означает «бюджета в файле нет» — это допустимый сценарий (например,
        // повторный импорт после ручной правки бюджета в Visary), ГФ выполняем.
        bool? budgetUploadOk = null;
        if (budgetRows.Count > 0)
        {
            // Pre-check: если в ИСР объекта уже есть WBS-узлы — бюджет повторно
            // не заливаем. Заказчик не хочет «перезатирать» уже сформированную ИСР
            // вторым typedimportwbs. ГФ Главы 1 запускаем сразу — узлы есть.
            // См. doc_project/109-finmodel-prechecks-wbs-and-gf.md.
            var schedulePending = scheduleArticleRows.Count > 0 && scheduleQuartersRow is not null;
            var wbsExists = await WbsAlreadyExistsForSiteAsync(siteId, errors, ct);
            if (wbsExists is null)
            {
                // listview/wbs упал — Pre-check считаем неуспешным и НЕ запускаем заливку
                // (иначе можем породить дубликат, если на самом деле WBS уже есть).
                // ГФ тоже пропускаем — без подтверждённого состояния ИСР это слепой POST.
                budgetUploadOk = false;
            }
            else if (wbsExists.Value)
            {
                _log.LogInformation(
                    "FinModelImportMapper: ИСР объекта siteId={SiteId} уже содержит WBS-узлы — заливка XLSX-бюджета пропущена",
                    siteId);
                errors.Add(new RowError(null, "budget_upload_skipped_wbs_exists",
                    "Импорт бюджета в Visary пропущен: ИСР объекта строительства уже сформирована (есть WBS-узлы). " +
                    (schedulePending
                        ? "ГФ Главы 1 будет применён к существующим статьям ИСР."
                        : "ГФ Главы 1 не запрашивался.")));
                budgetUploadOk = true;
            }
            else
            {
                // Бюджет в Visary заливается автоматически: BudgetXlsxExporter уже сохранил
                // mapped budget rows в staged_rows на стадии Validate; здесь поднимаем XLSX,
                // отправляем в файловое хранилище Visary, создаём typedimportwbs и ждём
                // финального статуса Visary'я. ГФ Главы 1 ниже создаёт CostItem-ы на WBS-узлах,
                // которые появляются в Visary именно по результатам этого импорта, поэтому
                // запускать ГФ до завершения бюджета бессмысленно (узлов ещё нет в ИСР).
                // Если Visary вернул «Закончен с ошибками» / timeout / сетевой сбой —
                // UploadBudgetToVisaryAsync пишет одну консолидированную row-error
                // (что сделано + причина Visary + «ГФ не создан»), и мы НЕ запускаем ГФ.
                // См. doc_project/82-visary-file-storage-upload.md и doc 94.
                budgetUploadOk = await UploadBudgetToVisaryAsync(
                    context.SessionId, budgetRows.Count, paramsApplied, schedulePending, errors, ct);
                if (budgetUploadOk.Value)
                    applied += budgetRows.Count;
            }
        }

        if (scheduleArticleRows.Count > 0 && scheduleQuartersRow is not null)
        {
            if (budgetUploadOk == false)
            {
                // Бюджет в Visary не доехал — WBS-узлов для ГФ ещё нет. Skip ГФ молча:
                // факт «ГФ Главы 1 не создан» уже включён в сообщение budget_upload_*.
                _log.LogWarning(
                    "FinModelImportMapper: бюджет в Visary не завершён успешно — ГФ Главы 1 пропущен (siteId={SiteId})",
                    siteId);
            }
            else
            {
                // ГФ Главы 1: для каждой mapped-статьи (1.1/1.6/1.8) находим WBS-узел объекта,
                // pre-check существующие CostItem и POST/PATCH/skip per quarter. Per-cell
                // RowAction — успех или «статья отсутствует в ИСР». См. doc 91.
                var scheduleApply = await ApplyChapter1ScheduleAsync(
                    siteId, scheduleQuartersRow, scheduleArticleRows, errors, rowActions, ct);
                applied += scheduleApply;
            }
        }
        else if (scheduleArticleRows.Count > 0)
        {
            _log.LogWarning(
                "FinModelImportMapper: schedule article rows={Count} но датовая строка не найдена — ГФ пропущен (siteId={SiteId})",
                scheduleArticleRows.Count, siteId);
        }

        // EnsureFmModelAsync вызван в начале ApplyAsync (до validRows-проверки) —
        // он ортогонален mapped-строкам и работает только по второму файлу.
        return new ApplyResult(applied, errors, rowActions.Count > 0 ? rowActions : null);
    }

    /// <summary>
    /// Создаёт <c>fmmodel</c> в Visary по краевым значениям листа «План» второго файла.
    /// Шаги:
    /// 1) Если второго файла нет — info <c>fmmodel_skipped_no_plan_file</c>, выходим.
    /// 2) Открываем XLSX через <see cref="IFileStorage"/>, ищем лист «План»,
    ///    читаем строку «Год» (r3) и строку «Квартал» (r5) — формат гарантирован
    ///    эталонной формой шаблона; ⚠️ номер строки «План» может варьироваться
    ///    среди файлов — сканируем первую строку, в которой A=«Год».
    /// 3) Forward-fill года: год лежит только в первой ячейке группы из 4 кварталов.
    /// 4) Краевые (year, quarter) → <c>"{Year}Q{N}"</c>.
    /// 5) Pre-check через <see cref="IListViewClient.FindFmModelsAsync"/> по
    ///    (ABProjectID, ABConstructionSiteID) — идемпотентность.
    /// 6) <see cref="ICrudClient.CreateFmModelAsync"/> при отсутствии.
    /// При любой ошибке — одна row-error, не валим Apply: остальные шаги (бюджет/ГФ)
    /// уже отработали выше. См. doc 110.
    /// </summary>
    private async Task EnsureFmModelAsync(
        int projectId,
        int siteId,
        string? secondaryFilePath,
        List<RowError> errors,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secondaryFilePath))
        {
            errors.Add(new RowError(null, "fmmodel_skipped_no_plan_file",
                "Файл с планами по фин. модели не загружен — Финмодель в Visary не создавалась. " +
                "Чтобы создать запись `fmmodel`, прикрепите второй файл с листом «План» " +
                "(строки «Год» и «Квартал»)."));
            return;
        }

        // 1. Парсим лист «Общий график»: краевой PeriodStart/PeriodEnd + материализованные
        //    InputData-точки (категория × период → Summ/Amount/Cost). На листе несколько
        //    таблиц (по одной на вид помещения); из каждой берём ТОЛЬКО первые 3 строки
        //    данных (План), второй блок «Факт» парсер пропускает.
        FinModelPlanData planData;
        try
        {
            await using var stream = await _fileStorage.OpenReadAsync(secondaryFilePath, ct);
            planData = ReadGeneralScheduleData(stream);
        }
        catch (FinModelPlanParseException ex)
        {
            errors.Add(new RowError(null, "fmmodel_plan_parse_error",
                $"Не удалось прочитать лист «Общий график» из второго файла: {ex.Message}"));
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FinModelImportMapper: failed to open/parse secondary general-schedule file (siteId={SiteId})", siteId);
            errors.Add(new RowError(null, "fmmodel_plan_parse_error",
                $"Не удалось прочитать второй файл (Общий график): {ex.Message}"));
            return;
        }

        // 2. Pre-check: уже есть Финмодель по (Title, ABConstructionSiteID, PeriodStart,
        //    PeriodEnd)? Один сайт может содержать НЕСКОЛЬКО Финмоделей с разными
        //    краевыми периодами (заказчик заводит отдельные модели на разные диапазоны
        //    лет). Без фильтра по периодам новый файл с PeriodStart=2023Q1 матчился
        //    бы с чужой моделью 2024Q1..2027Q4, и `inputdata` падала бы как новая
        //    версия чужой модели. См. doc 112 v1.3.
        int? existingFmModelId = null;
        try
        {
            var existing = await _listViewClient.FindFmModelsAsync(
                projectId, siteId, planData.PeriodStart, planData.PeriodEnd, ct);
            if (existing.Data is { Count: > 0 })
            {
                var first = existing.Data[0];
                existingFmModelId = first.ID;
                _log.LogInformation(
                    "FinModelImportMapper: fmmodel уже существует (id={Id}, period={Start}..{End}) — POST пропущен (projectId={ProjectId}, siteId={SiteId})",
                    first.ID, first.PeriodStart, first.PeriodEnd, projectId, siteId);
                errors.Add(new RowError(null, "fmmodel_skipped_already_exists",
                    $"Финмодель для проекта и объекта уже существует в Visary " +
                    $"(id={first.ID}, период {first.PeriodStart}..{first.PeriodEnd}). Создание fmmodel пропущено, " +
                    "версия и входные данные при необходимости будут досозданы."));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper: pre-check fmmodel failed (projectId={ProjectId}, siteId={SiteId}, period={Start}..{End}) — пропускаем создание",
                projectId, siteId, planData.PeriodStart, planData.PeriodEnd);
            errors.Add(new RowError(null, "fmmodel_precheck_failed",
                $"Не удалось проверить наличие Финмодели в Visary: {ex.Message}. " +
                "Создание пропущено, чтобы не породить дубликат."));
            return;
        }

        // 3. ProjectCode — берём Title проекта (в HAR-примере «Тест ДОУ»).
        //    Если Title недоступен — отправляем null (Visary поле опциональное).
        string? projectCode = null;
        try
        {
            var projectFull = await _visaryClient.GetProjectByIdFullAsync(projectId, ct);
            projectCode = projectFull?.Title;
        }
        catch (Exception ex)
        {
            // Не блокирует — ABProjectID и так в теле, проверим, что Visary возьмёт его как основу.
            _log.LogWarning(ex,
                "FinModelImportMapper: не удалось получить ProjectCode (projectId={ProjectId}) — продолжаем без него",
                projectId);
        }

        // 4. POST /crud/fmmodel (если pre-check не нашёл существующую).
        int fmModelId;
        if (existingFmModelId is { } existingId)
        {
            fmModelId = existingId;
        }
        else
        {
            try
            {
                var created = await _visaryClient.CreateFmModelAsync(new FmModelCreateRequest
                {
                    Title = FmModelTitle,
                    ProjectCode = projectCode,
                    ABProjectID = projectId,
                    ABConstructionSiteID = siteId,
                    PeriodStart = planData.PeriodStart,
                    PeriodEnd = planData.PeriodEnd,
                }, ct);
                fmModelId = created.ID;
                _log.LogInformation(
                    "FinModelImportMapper: fmmodel создан id={Id} period={Start}..{End} (projectId={ProjectId}, siteId={SiteId})",
                    created.ID, planData.PeriodStart, planData.PeriodEnd, projectId, siteId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "FinModelImportMapper: ошибка создания fmmodel (projectId={ProjectId}, siteId={SiteId}, period={Start}..{End})",
                    projectId, siteId, planData.PeriodStart, planData.PeriodEnd);
                errors.Add(new RowError(null, "fmmodel_create_failed",
                    $"Не удалось создать Финмодель в Visary " +
                    $"(период {planData.PeriodStart}..{planData.PeriodEnd}): {ex.Message}"));
                return;
            }
        }

        // 5. Версия Финмодели + входные данные + связь. Любая ошибка — single row-error,
        //    шаги-параметры/бюджет/ГФ выше уже отработали, не валим Apply целиком.
        await EnsureFmModelVersionAndInputDataAsync(
            fmModelId, planData, errors, ct);
    }

    /// <summary>
    /// Создаёт версию Финмодели и наполняет её «Входными данными» по листу «План».
    /// Идемпотентность:
    ///   • Версия — pre-check по Title «Версия - Перенос из Эксель» в
    ///     <see cref="IListViewClient.GetFmModelVersionsByModelAsync"/>; найдена — reuse.
    ///   • InputData — pre-check по (FMPeriod, Code.ID) в
    ///     <see cref="IListViewClient.GetInputDataByVersionAsync"/>; найдено — skip.
    /// Резолв кодов справочника <c>fmcode</c> — точечные запросы по уникальным Title
    /// (1 запрос на категорию = квартиры/нежилые/кладовки/м/м; обычно 3–4 запроса).
    /// Любая транспортная ошибка (сеть/500/таймаут) на резолве — row-error
    /// <c>inputdata_codes_unavailable</c> + skip версии и inputdata (fmmodel сохраняется).
    /// «Title не найден в справочнике» (0 строк в ответе) — row-error
    /// <c>inputdata_code_not_found</c> ПО ЗАВЕРШЕНИИ цикла, со списком всех missing.
    /// </summary>
    private async Task EnsureFmModelVersionAndInputDataAsync(
        int fmModelId,
        FinModelPlanData planData,
        List<RowError> errors,
        CancellationToken ct)
    {
        // 1) Резолв уникальных Title → ID через точечные запросы listview/fmcode.
        //    Не один большой запрос на весь справочник (как раньше с inputdatacode),
        //    потому что fmcode содержит сотни кодов — нам нужно 3–4 конкретных.
        //    Транспортная ошибка (любой запрос упал не из-за «не найдено») → выходим.
        var codeIdByTitle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missingCodeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueTitles = planData.InputDataPoints
            .Select(p => p.CodeTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var title in uniqueTitles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await _listViewClient.FindFmCodeByTitleAsync(title, ct);
                var found = resp.Data?.FirstOrDefault(c => c.ID > 0);
                if (found is not null)
                    codeIdByTitle[title] = found.ID;
                else
                    missingCodeTitles.Add(title);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "FinModelImportMapper: не удалось получить fmcode title='{Title}' (fmModelId={FmModelId})",
                    title, fmModelId);
                errors.Add(new RowError(null, "inputdata_codes_unavailable",
                    "Не удалось получить справочник «Код фин. модели» из Visary " +
                    $"(listview/fmcode, title=«{title}»): {ex.Message}. " +
                    "Версия Финмодели и входные данные не созданы."));
                return;
            }
        }

        // 2) Версия Финмодели — каждый импорт создаёт НОВУЮ версию с уникальным Title.
        //    Заказчик хочет историю переносов из Excel (каждый импорт = отдельная версия,
        //    «вторая версия с новыми данными»). По существующим версиям с тем же
        //    префиксом находим максимальный sequence-номер и инкрементируем; первая
        //    версия получает Title без номера, последующие — «… 2», «… 3» и т.д.
        int versionId;
        string newVersionTitle;
        try
        {
            var versions = await _listViewClient.GetFmModelVersionsByModelAsync(fmModelId, ct);
            newVersionTitle = BuildNextVersionTitle(versions.Data);
            var createdVersion = await _visaryClient.CreateFmModelVersionAsync(
                new FmModelVersionCreateRequest
                {
                    FMModelID = fmModelId,
                    FMModel = new VisaryRef { ID = fmModelId },
                    Title = newVersionTitle,
                }, ct);
            versionId = createdVersion.ID;
            _log.LogInformation(
                "FinModelImportMapper: fmmodelversion создан id={Id} title='{Title}' fmModelId={FmModelId}",
                versionId, newVersionTitle, fmModelId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper: не удалось создать fmmodelversion (fmModelId={FmModelId})",
                fmModelId);
            errors.Add(new RowError(null, "fmmodel_version_failed",
                $"Не удалось создать версию Финмодели в Visary: {ex.Message}. Входные данные не загружены."));
            return;
        }

        // 3) Новая версия — заведомо пустая, pre-check inputdata-by-version пропускаем.
        //    Раньше pre-check защищал от дубликатов при reuse-е версии; теперь, когда
        //    каждый импорт создаёт новую версию, дубликатов внутри неё не бывает.
        var existingPoints = new HashSet<(string FmPeriod, int CodeId)>();

        // 4) POST /crud/inputdata + link для каждой точки (категория × период).
        //    `missingCodeTitles` уже наполнен на шаге 1) — здесь только пропускаем
        //    точки тех категорий, для которых Title не нашёлся в справочнике.
        int createdCount = 0, skippedCount = 0, failedCount = 0;
        foreach (var point in planData.InputDataPoints)
        {
            ct.ThrowIfCancellationRequested();

            if (!codeIdByTitle.TryGetValue(point.CodeTitle, out var codeId))
            {
                // Уже учтено в missingCodeTitles на шаге 1 — просто скип точки.
                continue;
            }

            if (existingPoints.Contains((point.FmPeriod, codeId)))
            {
                skippedCount++;
                continue;
            }

            int inputDataId;
            try
            {
                var created = await _visaryClient.CreateInputDataAsync(
                    new InputDataCreateRequest
                    {
                        FMModelVersionID = versionId,
                        FMModelVersion = new VisaryRef { ID = versionId },
                        FMPeriod = point.FmPeriod,
                        Code = new VisaryRef { ID = codeId, Title = point.CodeTitle },
                        Summ = point.Summ,
                        Amount = point.Amount,
                        Cost = point.Cost,
                        Percent = 0d,
                    }, ct);
                inputDataId = created.ID;
            }
            catch (Exception ex)
            {
                failedCount++;
                _log.LogError(ex,
                    "FinModelImportMapper: ошибка создания inputdata (versionId={VersionId}, period={Period}, code='{Code}')",
                    versionId, point.FmPeriod, point.CodeTitle);
                continue;
            }

            // Линк inputdata → version (см. HAR заказчика). Любая ошибка — non-fatal.
            try
            {
                await _visaryClient.LinkInputDataToVersionAsync(versionId, inputDataId, ct);
                createdCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                _log.LogError(ex,
                    "FinModelImportMapper: ошибка привязки inputdata к версии (versionId={VersionId}, inputDataId={Id})",
                    versionId, inputDataId);
            }
        }

        if (missingCodeTitles.Count > 0)
        {
            errors.Add(new RowError(null, "inputdata_code_not_found",
                "В справочнике Visary «Код входных данных» не найдены записи: " +
                string.Join(", ", missingCodeTitles.Select(t => $"«{t}»")) +
                ". Часть «Входных данных» Финмодели не создана."));
        }

        if (failedCount > 0)
        {
            errors.Add(new RowError(null, "inputdata_create_failed",
                $"Не удалось создать/привязать {failedCount} записей `inputdata` в Visary. " +
                "См. лог приложения для деталей."));
        }

        _log.LogInformation(
            "FinModelImportMapper: inputdata загрузка завершена versionId={VersionId} created={Created} skipped={Skipped} failed={Failed}",
            versionId, createdCount, skippedCount, failedCount);
    }

    /// <summary>Видимое имя Финмодели в Visary (требование заказчика).</summary>
    private const string FmModelTitle = "Модель из эксель файла";

    /// <summary>
    /// Префикс Title-а версии Финмодели (требование заказчика, см. doc 112).
    /// Первая версия — ровно этот префикс; последующие — «… 2», «… 3» и т.д.
    /// </summary>
    internal const string FmModelVersionTitlePrefix = "Версия - Перенос из Эксель";

    /// <summary>
    /// Подбирает Title для НОВОЙ версии по списку уже существующих. Логика:
    /// <list type="number">
    ///   <item>Среди существующих ищем те, чей Title — ровно префикс
    ///     <see cref="FmModelVersionTitlePrefix"/> или «префикс N» (N — целое ≥ 2).</item>
    ///   <item>Берём максимальный N (отсутствие номера = N=1) и инкрементируем.</item>
    ///   <item>Если N&gt;=2 — Title «префикс N», иначе чистый префикс.</item>
    /// </list>
    /// Поведение: первая версия = «Версия - Перенос из Эксель»; вторая = «Версия - Перенос
    /// из Эксель 2»; третья = «Версия - Перенос из Эксель 3» и т.д. Title-конфликтов не
    /// должно быть (заказчик читает версии по этому префиксу).
    /// </summary>
    internal static string BuildNextVersionTitle(IReadOnlyList<FmModelVersionRaw>? existingVersions)
    {
        if (existingVersions is null || existingVersions.Count == 0)
            return FmModelVersionTitlePrefix;

        int maxSeq = 0;
        bool anyMatched = false;
        foreach (var v in existingVersions)
        {
            var t = v?.Title?.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (!t.StartsWith(FmModelVersionTitlePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            anyMatched = true;
            var rest = t.Substring(FmModelVersionTitlePrefix.Length).Trim();
            if (rest.Length == 0)
            {
                // Ровно префикс — sequence #1.
                if (maxSeq < 1) maxSeq = 1;
                continue;
            }
            if (int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 2)
            {
                if (n > maxSeq) maxSeq = n;
            }
            // Прочие хвосты (произвольный текст после префикса) — игнорируем, не считаем
            // в seq, но всё ещё формально учитываем как «версия с этим префиксом существует».
        }

        if (!anyMatched) return FmModelVersionTitlePrefix;
        var next = maxSeq + 1;
        return next <= 1
            ? FmModelVersionTitlePrefix
            : $"{FmModelVersionTitlePrefix} {next}";
    }

    /// <summary>
    /// Парсит лист «Общий график» XLSX-файла и возвращает данные для создания
    /// Финмодели, её Версии и «Входных данных». В файлах заказчика на этом листе
    /// может быть несколько таблиц — по одной на вид помещения (Квартиры,
    /// Нежилые-ПСН, Кладовки, Машиноместа). Каждая таблица имеет идентичную форму:
    /// <list type="bullet">
    ///   <item>строка <c>A=«Год»</c> с годами в C/D…</item>
    ///   <item>следующая строка <c>A=«Квартал»</c>, <c>B=«Сумма»</c>, далее «1 кв».. «4 кв»</item>
    ///   <item>опц. строка-маркер <c>A=«План»</c></item>
    ///   <item>«Площадь, кв.м» / «Машиноместа, шт.» / «Нежилые, кв.м» / … — A-текст содержит
    ///     имя категории (по нему резолвится Code в справочнике <c>fmcode</c>);
    ///     значения колонок = плановая площадь/количество (Amount)</item>
    ///   <item>следующая строка = стоимость 1 ед. (Cost) — A может быть <c>#GETTING_DATA</c></item>
    ///   <item>следующая строка = сумма дохода (Summ) — A-текст обычно «Доход» или «Сумма»</item>
    ///   <item>строка «Доход накопл. Итогом» — игнорируется (cumulative)</item>
    ///   <item>строка-маркер «Факт» + следующие 4 строки = фактические данные — ВСЕ игнорируются</item>
    /// </list>
    /// Парсер берёт только ПЕРВЫЕ ТРИ строки данных (план), фактический блок отсекается.
    /// См. doc_project/112-finmodel-version-and-inputdata.md.
    /// </summary>
    internal static FinModelPlanData ReadGeneralScheduleData(Stream xlsxStream)
    {
        // Читаем поток В МАССИВ БАЙТ один раз: ClosedXML на ошибке в ctor закрывает
        // переданный Stream, поэтому retry поверх того же MemoryStream упадёт с
        // ObjectDisposedException. См. doc 81 (XlsxParser применяет тот же паттерн).
        byte[] bytes;
        using (var src = new MemoryStream())
        {
            xlsxStream.CopyTo(src);
            bytes = src.ToArray();
        }

        try
        {
            return ReadGeneralScheduleDataFromBytes(bytes);
        }
        catch (Exception ex) when (XlsxParser.IsExternalLinkError(ex))
        {
            // Шаблоны заказчика часто содержат формулы с external-links на сетевые
            // файлы («file:///\\Alt/intern/.../[XYZ.xls]Sheet»). ClosedXML на них падает
            // «Unable to determine token». Чистим zip от external-частей (см. doc 81)
            // и пробуем ещё раз — кэшированные значения в <v> остаются, поэтому
            // ячейки с числами читаются успешно.
            var cleaned = XlsxParser.StripExternalLinks(bytes);
            return ReadGeneralScheduleDataFromBytes(cleaned);
        }
    }

    /// <summary>
    /// Открывает байты как <see cref="XLWorkbook"/> и читает «Общий график»
    /// целиком (краевые периоды + все таблицы по видам помещений + точки InputData).
    /// Любая ошибка летит наружу — внешний retry ловит её для external-link cleanup.
    /// </summary>
    private static FinModelPlanData ReadGeneralScheduleDataFromBytes(byte[] bytes)
    {
        // writable: false — ClosedXML не испортит байты на случай retry.
        using var ms = new MemoryStream(bytes, writable: false);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheets.FirstOrDefault(ws =>
            string.Equals(ws.Name?.Trim(), GeneralScheduleSheetName, StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
        {
            throw new FinModelPlanParseException(
                $"Во втором файле не найден лист «{GeneralScheduleSheetName}». Доступные листы: " +
                string.Join(", ", wb.Worksheets.Select(w => $"«{w.Name}»")));
        }

        var sheetRange = sheet.RangeUsed();
        var lastUsedRow = sheetRange?.LastRow().RowNumber() ?? 200;
        var lastUsedColumn = sheetRange?.LastColumn().ColumnNumber() ?? 30;

        // Скан всех таблиц: для каждой строки A=«Год», следом — A=«Квартал» (допускается
        // 1 промежуточная строка между ними; в файлах заказчика это помесячная подшапка
        // с дублированными годами в C..). После заголовка ищем 3 строки данных
        // (Площадь/Cost/Summ), пропуская опциональный маркер «План». На «Факт»/«Доход
        // накопл.» — стоп (фактический блок не нужен).
        //
        // Категория (вид помещения) резолвится по двум источникам, в порядке приоритета:
        //   (1) Шапка-«Тип помещения»: A-колонка строки <=5 ВЫШЕ «Год» содержит «Тип
        //       помещения», а в B-колонке — название («Квартиры»/«Нежилое»/«Кладовые»/
        //       «Машиноместа»). Раскладка из «Репино-Парк»: A-row генерик («Площадь, кв.м»),
        //       без шапки таблица бы не резолвилась.
        //   (2) Fallback — A-текст самой Amount-строки содержит маркер («Квартиры, кв.м»,
        //       «Нежилые помещения, кв.м», …). Раскладка из «Журавли», тестовый фикстур.
        var allColumns = new Dictionary<int, FinModelPlanColumn>();
        var categories = new List<FinModelPlanCategory>();
        var points = new List<FinModelPlanInputDataPoint>();
        for (int r = 1; r <= lastUsedRow - 1; r++)
        {
            var aText = sheet.Cell(r, 1).GetString().Trim();
            if (!string.Equals(aText, "Год", StringComparison.OrdinalIgnoreCase)) continue;

            // «Квартал» допускается на r+1 ИЛИ r+2 (между ними может быть помесячная
            // подшапка с дублированными годами).
            int quarterRow = -1;
            for (int rr = r + 1; rr <= Math.Min(r + 2, lastUsedRow); rr++)
            {
                var rrText = sheet.Cell(rr, 1).GetString().Trim();
                if (string.Equals(rrText, "Квартал", StringComparison.OrdinalIgnoreCase))
                {
                    quarterRow = rr;
                    break;
                }
            }
            if (quarterRow < 0) continue;

            int yearRow = r;

            // Шапка-«Тип помещения» — ищем строго ВЫШЕ Год-строки (≤5 строк назад),
            // чтобы не подцепить шапку следующей таблицы.
            string? codeTitleFromHeader = null;
            int headerScanStart = Math.Max(1, yearRow - 5);
            for (int hr = yearRow - 1; hr >= headerScanStart; hr--)
            {
                var hA = sheet.Cell(hr, 1).GetString().Trim();
                if (string.IsNullOrEmpty(hA)) continue;
                if (hA.IndexOf("Тип помещения", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // Значение справа — может быть в любой колонке от B (2) до E (5).
                for (int hc = 2; hc <= 5; hc++)
                {
                    var hVal = sheet.Cell(hr, hc).GetString().Trim();
                    if (string.IsNullOrEmpty(hVal)) continue;
                    codeTitleFromHeader = ResolveInputDataCodeTitle(hVal.ToLowerInvariant());
                    if (codeTitleFromHeader is not null) break;
                }
                break;
            }

            // Колонки этой таблицы: сканируем слева направо, forward-fill года.
            var tableColumns = new List<FinModelPlanColumn>();
            int yearCarry = 0;
            for (int c = 3; c <= lastUsedColumn; c++)
            {
                var yearText = sheet.Cell(yearRow, c).GetString().Trim();
                if (int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                    yearCarry = y;

                var quarterText = sheet.Cell(quarterRow, c).GetString().Trim();
                var quarterN = ParseQuarter(quarterText);
                if (yearCarry == 0 || quarterN is null) continue;

                tableColumns.Add(new FinModelPlanColumn(c, $"{yearCarry}Q{quarterN}"));
            }
            if (tableColumns.Count == 0) continue;

            // Ищем Площадь-строку. Если категория уже резолвлена из шапки — берём ПЕРВУЮ
            // содержательную строку после quarterRow (skip «План»-маркер, stop на
            // «Факт»/«накопл»). Если шапки нет — fallback: А-текст самой Amount-строки
            // должен содержать маркер категории.
            string? codeTitle = codeTitleFromHeader;
            int amountRow = -1;
            int scanLimit = Math.Min(quarterRow + 8, lastUsedRow);
            for (int rr = quarterRow + 1; rr <= scanLimit; rr++)
            {
                var aRaw = sheet.Cell(rr, 1).GetString().Trim();
                if (string.IsNullOrEmpty(aRaw)) continue;
                var aLower = aRaw.ToLowerInvariant();
                // Stop-маркеры — фактический блок начался или идёт накопительный
                // итог — Площадь-строки в этой таблице больше нет.
                if (aLower.StartsWith("факт")) break;
                if (aLower.Contains("накопл")) break;
                // Маркер начала плана — просто заголовок-разделитель, не строка данных.
                if (string.Equals(aLower, "план", StringComparison.Ordinal)) continue;

                if (codeTitle is null)
                {
                    // Layout-2 (Журавли/тестовый фикстур): категория зашита в А-текст
                    // самой Amount-строки.
                    codeTitle = ResolveInputDataCodeTitle(aLower);
                    if (codeTitle is not null)
                    {
                        amountRow = rr;
                        break;
                    }
                    // Ни шапки, ни маркера в А-тексте — пробуем следующую строку.
                    continue;
                }

                // Layout-1 (Репино-Парк): категория из шапки; Amount-строка — первая
                // содержательная после quarterRow («Площадь, кв.м»/«Колич-во м/м»/…).
                amountRow = rr;
                break;
            }
            if (codeTitle is null || amountRow < 0) continue;

            int costRow = amountRow + 1;
            int summRow = amountRow + 2;

            // Анти-дубликат: если в одном файле подряд два блока одной категории
            // (что встречается у заказчика как «План»/«Факт» половины одной и той же
            // таблицы) — оставляем только первый.
            if (categories.Any(cat => cat.CodeTitle == codeTitle)) continue;

            categories.Add(new FinModelPlanCategory(codeTitle, AmountRow: amountRow, CostRow: costRow, SummRow: summRow));

            // Сразу материализуем точки этой категории — пока worksheet открыт.
            foreach (var col in tableColumns)
            {
                points.Add(new FinModelPlanInputDataPoint(
                    FmPeriod: col.FmPeriod,
                    CodeTitle: codeTitle,
                    Summ:   ReadPlanCellNumber(sheet, summRow,   col.ColumnIndex),
                    Amount: ReadPlanCellNumber(sheet, amountRow, col.ColumnIndex),
                    Cost:   ReadPlanCellNumber(sheet, costRow,   col.ColumnIndex)));
                allColumns.TryAdd(col.ColumnIndex, col);
            }
        }

        if (categories.Count == 0)
        {
            throw new FinModelPlanParseException(
                $"На листе «{GeneralScheduleSheetName}» не найдено ни одной таблицы. " +
                "Ожидается пара строк «Год»/«Квартал» (возможно с помесячной подшапкой между ними) " +
                "и название вида помещения — либо в шапке «Тип помещения» (B-колонка) " +
                "ВЫШЕ строки «Год», либо в A-тексте Площадь/Колич-во-строки " +
                "(«Квартиры …»/«Нежилые …»/«Кладовки …»/«Машиноместа …»).");
        }

        // Краевые периоды — по объединению всех таблиц (FmPeriod отсортирован
        // лексикографически: «2024Q1» < «2024Q2» < … < «2027Q4»).
        var orderedColumns = allColumns.Values
            .OrderBy(c => c.FmPeriod, StringComparer.Ordinal)
            .ToList();
        return new FinModelPlanData(
            PeriodStart: orderedColumns.First().FmPeriod,
            PeriodEnd: orderedColumns.Last().FmPeriod,
            Columns: orderedColumns,
            Categories: categories,
            InputDataPoints: points);
    }

    /// <summary>
    /// Резолв категории InputData по тексту в A-колонке Площадь-строки.
    /// ⚠️ Порядок важен: «иные нежилые/кладовки» проверяются ДО общего «нежил».
    /// </summary>
    private static string? ResolveInputDataCodeTitle(string aLower)
    {
        if (aLower.Contains("кварт"))
            return InputDataCodeApartment;
        if (aLower.Contains("кладов") || aLower.Contains("иные нежил"))
            return InputDataCodeStoreroom;
        if (aLower.Contains("нежил"))
            return InputDataCodeNonResidential;
        if (aLower.Contains("м/м") || aLower.Contains("машином"))
            return InputDataCodeParking;
        return null;
    }

    /// <summary>Имя листа в шаблоне заказчика, на котором лежат таблицы Финмодели.</summary>
    private const string GeneralScheduleSheetName = "Общий график";

    /// <summary>
    /// «1 кв»/«2 кв»/«3 кв»/«4 кв»/«1кв»/«1 квартал» → 1..4. Любое отклонение → null.
    /// ⚠️ В файлах заказчика на листе «Общий график» соседствует помесячная таблица,
    /// где в строке «Квартал» стоят номера месяцев «10», «11», «12». Раньше парсер
    /// возвращал на «10» цифру 1 (первая 1..4 в строке) — это попадало в data
    /// как Q1, и помесячные строки засоряли inputdata. Поэтому проверяем
    /// «после цифры идёт пробел/буква/конец строки, а НЕ ещё одна цифра».
    /// </summary>
    internal static int? ParseQuarter(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length == 0) return null;
        var first = s[0];
        if (first < '1' || first > '4') return null;
        // Однозначный «1»/«2»/«3»/«4» — OK; либо после цифры идёт нецифровой символ
        // («1 кв», «1кв», «1 квартал», «1.»). Многозначные числа («10», «12») — NOT a quarter.
        if (s.Length == 1) return first - '0';
        if (char.IsDigit(s[1])) return null;
        return first - '0';
    }

    /// <summary>
    /// Считает ячейку с числом из «Плана» и возвращает плановое значение
    /// (нули — валидное значение, см. doc 112 §3). Текст «#DIV/0!»/пустое/невалидное
    /// число → 0.
    /// </summary>
    internal static double ReadPlanCellNumber(IXLWorksheet sheet, int row, int col)
    {
        var cell = sheet.Cell(row, col);
        if (cell.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return d;
        var text = cell.GetString().Trim();
        if (string.IsNullOrWhiteSpace(text)) return 0d;
        // Замена запятой на точку для русской локали + игнор «#DIV/0!»/прочих error-strings.
        text = text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : 0d;
    }

    // Title справочника fmcode (HAR заказчика). ID не хардкодятся — резолв
    // через listview/fmcode (см. FindFmCodeByTitleAsync), Title зашит как контракт
    // между листом «План» и справочником fmcode.
    // ⚠️ Внимание на пробелы: «Продажа нежилые ( ком) ПСН (план)» имеет лишний пробел
    // после «(» — это точное написание из Visary, без него Title не находится по «=».
    internal const string InputDataCodeApartment       = "Продажа квартиры (план)";
    internal const string InputDataCodeNonResidential  = "Продажа нежилые ( ком) ПСН (план)";
    internal const string InputDataCodeStoreroom       = "Продажа иные нежилые (кладовки) (план)";
    internal const string InputDataCodeParking         = "Продажа м/м (план)";

    internal sealed record FinModelPlanPeriods(string PeriodStart, string PeriodEnd);

    /// <summary>Одна колонка периода в листе «План» (после forward-fill года).</summary>
    internal sealed record FinModelPlanColumn(int ColumnIndex, string FmPeriod);

    /// <summary>
    /// Триплет строк (Amount, Cost, Summ) для одной категории InputData
    /// (вид помещения). Номера строк — абсолютные в XLSX.
    /// </summary>
    internal sealed record FinModelPlanCategory(
        string CodeTitle, int AmountRow, int CostRow, int SummRow);

    /// <summary>
    /// Полная распарсенная картина листа «План»: краевые периоды + столбцы кварталов
    /// + категории по видам помещений + материализованные точки InputData. Точки
    /// собираются ВНУТРИ парсера, пока <see cref="XLWorkbook"/> ещё открыт — после
    /// выхода из using-области в <see cref="ReadPlanDataFromBytes"/> чтение ячеек
    /// уже невозможно (поэтому отдельной lazy-функции <c>MaterializeInputData</c>
    /// тут НЕТ — это сознательное решение, см. doc 112 §6 «жизненный цикл ClosedXML»).
    /// </summary>
    internal sealed record FinModelPlanData(
        string PeriodStart,
        string PeriodEnd,
        IReadOnlyList<FinModelPlanColumn> Columns,
        IReadOnlyList<FinModelPlanCategory> Categories,
        IReadOnlyList<FinModelPlanInputDataPoint> InputDataPoints);

    /// <summary>
    /// Одна готовая InputData-точка для POST <c>/crud/inputdata</c>:
    /// период × вид помещения × тройка чисел из листа «План».
    /// </summary>
    internal sealed record FinModelPlanInputDataPoint(
        string FmPeriod, string CodeTitle, double Summ, double Amount, double Cost);

    internal sealed class FinModelPlanParseException : Exception
    {
        public FinModelPlanParseException(string message) : base(message) { }
    }

    /// <summary>
    /// Pre-check перед заливкой XLSX-бюджета: в ИСР выбранного объекта уже есть
    /// WBS-узлы? Возвращает <c>true</c> (есть, заливку пропускаем), <c>false</c>
    /// (нет, заливаем) или <c>null</c> при ошибке listview/wbs (тогда заливать не
    /// безопасно — может быть и есть, и нет). См. doc 109.
    /// </summary>
    private async Task<bool?> WbsAlreadyExistsForSiteAsync(
        int siteId, List<RowError> errors, CancellationToken ct)
    {
        try
        {
            var wbs = await _listViewClient.GetWbsBySiteAsync(siteId, ct);
            return wbs.Data is { Count: > 0 };
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper: pre-check WBS-by-site failed (siteId={SiteId}) — заливка бюджета и ГФ пропущены",
                siteId);
            errors.Add(new RowError(null, "budget_upload_precheck_failed",
                $"Не удалось проверить наличие ИСР объекта строительства (siteId={siteId}): {ex.Message}. " +
                "Заливка бюджета и ГФ Главы 1 пропущены, чтобы не создать дубликат WBS."));
            return null;
        }
    }

    /// <summary>
    /// Заливает сгенерированный XLSX бюджета в файловое хранилище Visary, создаёт
    /// <c>typedimportwbs</c> и дожидается финального статуса. Возвращает <c>true</c>,
    /// если Visary вернул «Закончен успешно» либо «Закончен с предупреждениями» (оба
    /// разрешают запуск импорта ГФ); иначе — <c>false</c> с одной консолидированной
    /// row-error: что было сделано до бюджета + причина от Visary + явное упоминание,
    /// что ГФ Главы 1 не созданы (если они были запланированы).
    /// </summary>
    /// <remarks>
    /// <see cref="BudgetVisaryUploader"/> зарегистрирован Scoped (зависит от
    /// <c>ImportServiceDbContext</c>), а мапер — Singleton, поэтому открываем мини-scope
    /// через <see cref="IServiceScopeFactory"/>. Сам uploader держит логику upload+poll.
    /// </remarks>
    private async Task<bool> UploadBudgetToVisaryAsync(
        Guid sessionId,
        int budgetRowsCount,
        bool paramsApplied,
        bool schedulePending,
        List<RowError> errors,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uploader = scope.ServiceProvider.GetRequiredService<IBudgetVisaryUploader>();
            var result = await uploader.UploadAndWaitAsync(sessionId, ct: ct);

            if (result.Success)
            {
                _log.LogInformation(
                    "FinModelImportMapper: бюджет залит в Visary (typedImportWbsId={Id}, status='{Status}', errors={Errors}, warnings={Warnings}, budgetRows={Rows})",
                    result.Upload.TypedImportWbsId, result.FinalStatus,
                    result.CountErrors, result.CountWarnings, budgetRowsCount);
                return true;
            }

            var summary = BuildBudgetFailureSummary(
                paramsApplied, schedulePending, result.Upload.TypedImportWbsId,
                result.FinalStatus, result.CountErrors, result.CountWarnings,
                result.TimedOut, exceptionMessage: null);
            errors.Add(new RowError(null,
                result.TimedOut ? "budget_upload_timeout" : "budget_upload_failed",
                summary));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FinModelImportMapper: ошибка автоматической загрузки бюджета (sessionId={SessionId})", sessionId);
            var summary = BuildBudgetFailureSummary(
                paramsApplied, schedulePending, typedImportWbsId: null,
                finalStatus: null, countErrors: null, countWarnings: null,
                timedOut: false, exceptionMessage: ex.Message);
            errors.Add(new RowError(null, "budget_upload_error", summary));
            return false;
        }
    }

    /// <summary>
    /// Формирует единое сообщение о завершении импорта при провале бюджета. Три блока:
    /// (1) что было сделано до бюджета (параметры объекта применены / не применялись);
    /// (2) почему импорт бюджета не прошёл — статус Visary + counts либо текст исключения;
    /// (3) ГФ Главы 1 не созданы (если они были запланированы).
    /// </summary>
    private static string BuildBudgetFailureSummary(
        bool paramsApplied,
        bool schedulePending,
        int? typedImportWbsId,
        string? finalStatus,
        int? countErrors,
        int? countWarnings,
        bool timedOut,
        string? exceptionMessage)
    {
        var sb = new System.Text.StringBuilder();

        // (1) Что сделано
        sb.Append("Импорт Финмодели завершён. ");
        sb.Append(paramsApplied
            ? "Параметры объекта строительства (отделка, класс жилья, адрес, показатели) применены."
            : "Параметры объекта строительства не применялись.");

        // (2) Почему не прошёл бюджет
        sb.Append(' ');
        if (exceptionMessage is not null)
        {
            sb.Append($"Импорт бюджета в Visary не выполнен: {exceptionMessage}.");
        }
        else if (timedOut)
        {
            sb.Append("Импорт бюджета в Visary не завершился за отведённое время");
            if (typedImportWbsId is not null)
                sb.Append($" (typedimportwbs ID={typedImportWbsId.Value}");
            if (!string.IsNullOrWhiteSpace(finalStatus))
                sb.Append($"{(typedImportWbsId is not null ? ", " : " (")}последний статус: «{finalStatus}»");
            if (typedImportWbsId is not null || !string.IsNullOrWhiteSpace(finalStatus))
                sb.Append(')');
            sb.Append('.');
        }
        else
        {
            sb.Append($"Импорт бюджета в Visary завершился со статусом «{finalStatus ?? "—"}»");
            var hasErr = countErrors is not null && countErrors > 0;
            var hasWarn = countWarnings is not null && countWarnings > 0;
            if (hasErr || hasWarn)
            {
                sb.Append(" (");
                if (hasErr) sb.Append($"ошибок: {countErrors}");
                if (hasErr && hasWarn) sb.Append(", ");
                if (hasWarn) sb.Append($"предупреждений: {countWarnings}");
                sb.Append(')');
            }
            sb.Append('.');
            if (typedImportWbsId is not null)
                sb.Append($" Детали — в карточке typedimportwbs ID={typedImportWbsId.Value} в Visary.");
        }

        // (3) ГФ
        sb.Append(' ');
        sb.Append(schedulePending
            ? "ГФ Главы 1 не создан, так как WBS-узлы появляются в Visary только после успешного импорта бюджета."
            : "ГФ Главы 1 не запрашивался.");

        return sb.ToString();
    }

    /// <summary>
    /// Применяет параметрические обновления (FK + indicators + Address) — по бизнесу
    /// у нас ОДНА «логическая» строка для Site (даже если этапов несколько). Берём
    /// первую валидную и игнорируем остальные.
    /// </summary>
    private async Task<int> ApplyParametersAsync(
        int siteId, IReadOnlyList<MappedRow> paramRows, List<RowError> errors, CancellationToken ct)
    {
        var firstRow = paramRows[0];
        // Привязка row-level Apply-ошибок к конкретной params-строке: фронт по этим
        // полям сгруппирует ошибки в нужной строке листа Inputs (см. doc 100).
        // ApplyParametersAsync работает с ОДНОЙ логической строкой params (firstRow),
        // поэтому одна точка привязки.
        var paramRow = firstRow.SourceRowNumber;
        var paramSheet = firstRow.Sheet;
        RowError ParamError(string code, string message, string? column = null) =>
            new(column, code, message, paramRow, paramSheet);

        var root = firstRow.MappedValues.RootElement;
        var finishingMaterialId = root.GetProperty("FinishingMaterialId").GetInt32();
        var estateClassId       = root.GetProperty("EstateClassId").GetInt32();
        var address             = root.TryGetProperty("Address", out var addrEl)
                                  && addrEl.ValueKind == JsonValueKind.String
                                  ? addrEl.GetString()
                                  : null;
        var inn                 = root.TryGetProperty("Inn", out var innEl)
                                  && innEl.ValueKind == JsonValueKind.String
                                  ? innEl.GetString()
                                  : null;
        var borrowerTitle       = root.TryGetProperty("BorrowerTitle", out var btEl)
                                  && btEl.ValueKind == JsonValueKind.String
                                  ? btEl.GetString()
                                  : null;
        var companyGroupTitle   = root.TryGetProperty("CompanyGroupTitle", out var cgEl)
                                  && cgEl.ValueKind == JsonValueKind.String
                                  ? cgEl.GetString()
                                  : null;

        try
        {
            await _visaryClient.UpdateSiteFinishingMaterialAsync(siteId, finishingMaterialId, ct);
            await _visaryClient.UpdateSiteEstateClassAsync(siteId, estateClassId, ct);
            if (!string.IsNullOrWhiteSpace(address))
                await _visaryClient.UpdateSiteAddressAsync(siteId, address, ct);

            // Раздел «Основные данные»: при наличии ИНН + наименования —
            // найти/создать Organization и привязать её к проекту через PM-запись
            // (Заемщик/Застройщик). Изолировано от остальных параметров: одна ошибка
            // здесь не отменяет уже применённые FK/Address-обновления.
            int? linkedOrgId = null;
            if (!string.IsNullOrWhiteSpace(inn) && !string.IsNullOrWhiteSpace(borrowerTitle))
            {
                try
                {
                    linkedOrgId = await LinkBorrowerOrganizationAsync(siteId, inn!, borrowerTitle!, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex,
                        "FinModelImportMapper: organization/PM link failed for siteId={SiteId} inn={Inn}",
                        siteId, inn);
                    errors.Add(ParamError("organization_link_error",
                        $"Ошибка привязки организации '{borrowerTitle}' (ИНН {inn}) к проекту: {ex.Message}"));
                }
            }

            // ГК-flow: если у нас есть orgId (организация найдена/создана) и в файле
            // указано наименование ГК — пытаемся проставить Group у Organization.
            // Изолируем — отдельные row-error'ы (skip/not-found/multiple-found/patch-fail),
            // которые не отменяют уже применённые параметры. См. doc 100.
            if (linkedOrgId is int orgIdForGroup
                && !string.IsNullOrWhiteSpace(companyGroupTitle))
            {
                try
                {
                    await LinkCompanyGroupAsync(orgIdForGroup, companyGroupTitle!, errors, ParamError, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex,
                        "FinModelImportMapper: company-group link failed for orgId={OrgId} title='{Title}'",
                        orgIdForGroup, companyGroupTitle);
                    errors.Add(ParamError("company_group_link_error",
                        $"ГК не найдена, тк ошибка обновления организации: {ex.Message}"));
                }
            }

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
                        errors.Add(ParamError("indicator_not_found", ex.Message));
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Indicator '{Param}' update failed for siteId={SiteId}", param.HumanName, siteId);
                        errors.Add(ParamError("indicator_update_error",
                            $"Ошибка обновления показателя '{param.HumanName}': {ex.Message}"));
                    }
                }
            }

            _log.LogInformation(
                "FinModelImportMapper.ApplyParametersAsync: SiteId={SiteId} FinishingMaterialId={Fm} EstateClassId={Ec} Address='{Address}' indicators={Indicators}",
                siteId, finishingMaterialId, estateClassId, address ?? "(не задан)", Indicators.Length);

            return errors.Count == 0 ? 1 : 0;
        }
        catch (KeyNotFoundException ex)
        {
            _log.LogError(ex, "Visary site not found for siteId={SiteId}", siteId);
            errors.Add(ParamError("visary_site_not_found",
                $"Объект строительства {siteId} не найден в Visary."));
            return 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Visary update failed for siteId={SiteId}", siteId);
            errors.Add(ParamError("visary_update_error",
                $"Ошибка обновления в Visary: {ex.Message}"));
            return 0;
        }
    }

    /// <summary>
    /// Ensure-семантика: до записей в Объект гарантируем наличие сделки (Deal) в выбранном
    /// проекте по <c>DocNumber</c>. Если сделка найдена в проекте — продолжаем; если
    /// глобально нашлась, но в другом проекте — row-error и skip параметров; если нигде
    /// нет — СОЗДАЁМ её через POST <c>/api/visary/crud/deal</c> и продолжаем. Возвращает
    /// <c>true</c>, если сделка есть в этом проекте (найдена или создана); <c>false</c> —
    /// если найдена в чужом проекте либо если listview/create-вызов упал.
    /// </summary>
    /// <remarks>
    /// История поведения:
    /// <list type="bullet">
    ///   <item>v1.0 (2026-05-21): отсутствие сделки → row-error «deal_not_found» + skip Apply.</item>
    ///   <item>v1.1 (2026-05-21): по уточнению заказчика — сделку создаём сами с
    ///         минимальным payload. <c>Title:"-"</c> — временный костыль.</item>
    ///   <item>v1.2 (2026-05-21): между «не нашли в проекте» и «создаём» добавлен
    ///         глобальный fallback-listview; «сделка в чужом проекте» → row-error
    ///         <c>deal_in_other_project</c> + skip Apply.</item>
    ///   <item>v1.3 (2026-05-21): <c>DocNumber</c> теперь читается с управляющего листа
    ///         «Control» (поле «Номер КД») — см. <see cref="ControlValueRef"/>. LmID
    ///         больше не передаётся в фильтрах и payload (по запросу заказчика);
    ///         сравнение и create — только по <c>DocNumber</c>.</item>
    /// </list>
    /// </remarks>
    private async Task<bool> EnsureDealExistsInProjectAsync(
        int siteId,
        IReadOnlyList<MappedRow> paramRows,
        VisaryDbContext visaryDb,
        List<RowError> errors,
        List<RowActionLog> rowActions,
        CancellationToken ct)
    {
        var firstRow = paramRows[0];
        var root = firstRow.MappedValues.RootElement;
        var docNumber = root.TryGetProperty("DocNumber", out var dnEl)
                        && dnEl.ValueKind == JsonValueKind.String
                        ? dnEl.GetString()
                        : null;

        // «Номер договора» отсутствует/пуст (нет строки «Номер КД» в Control, или ячейка
        // пустая) — pre-check не делаем. Шаблоны без этого поля продолжают работать.
        if (string.IsNullOrWhiteSpace(docNumber))
            return true;

        // Резолвим Project Объекта. Visary mirror в локальном Postgres всегда содержит
        // ConstructionProjectId для Site (поле NOT NULL в схеме Data."ConstructionSite").
        var projectId = await visaryDb.ConstructionSites
            .Where(s => s.Id == siteId)
            .Select(s => (int?)s.ConstructionProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is null || projectId == 0)
        {
            _log.LogWarning(
                "FinModelImportMapper: deal pre-check skipped — projectId не найден для siteId={SiteId}",
                siteId);
            return true;
        }

        ListViewResponse<DealRaw> deals;
        try
        {
            deals = await _listViewClient.GetDealsByProjectAsync(
                projectId.Value, lmIdFilter: null, docNumberFilter: docNumber, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper: deal pre-check call failed for projectId={ProjectId} docNumber='{DocNumber}'",
                projectId.Value, docNumber);
            foreach (var pr in paramRows)
            {
                errors.Add(new RowError(
                    "Номер договора", "deal_check_error",
                    $"Не удалось проверить сделку в проекте (№={docNumber}): {ex.Message}",
                    pr.SourceRowNumber, pr.Sheet));
            }
            return false;
        }

        var match = deals.Data.FirstOrDefault(d =>
            string.Equals(d.DocNumber?.Trim(), docNumber.Trim(), StringComparison.Ordinal));

        if (match is not null)
        {
            _log.LogInformation(
                "FinModelImportMapper: deal found in projectId={ProjectId} — dealId={DealId}, Title='{Title}'",
                projectId.Value, match.ID, match.Title);
            rowActions.Add(new RowActionLog(
                firstRow.SourceRowNumber, firstRow.Sheet,
                new[] { $"Сделка найдена в проекте: ID={match.ID}, № «{match.DocNumber}»." }));
            return true;
        }

        // Сделки нет в текущем проекте — пробуем найти её глобально (см. doc 104 v1.2).
        // Если она существует в чужом проекте, в Visary нельзя «перепривязать» сделку
        // импортом, и нельзя создать дубликат с тем же DocNumber — поэтому выходим
        // с row-error и пропускаем Apply параметров.
        ListViewResponse<DealRaw> globalDeals;
        try
        {
            globalDeals = await _listViewClient.GetDealsAsync(
                lmIdFilter: null, docNumberFilter: docNumber, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper: deal global pre-check failed (docNumber='{DocNumber}')",
                docNumber);
            foreach (var pr in paramRows)
            {
                errors.Add(new RowError(
                    "Номер договора", "deal_check_error",
                    $"Не удалось проверить сделку в общем списке (№={docNumber}): {ex.Message}",
                    pr.SourceRowNumber, pr.Sheet));
            }
            return false;
        }

        var globalMatch = globalDeals.Data.FirstOrDefault(d =>
            string.Equals(d.DocNumber?.Trim(), docNumber.Trim(), StringComparison.Ordinal));

        if (globalMatch is not null)
        {
            var otherProjectId    = globalMatch.ConstructionProject?.ID;
            var otherProjectTitle = globalMatch.ConstructionProject?.Title;
            // Текст ошибки делаем самодостаточным — пользователь должен понять, в каком
            // именно проекте уже живёт «его» DocNumber, чтобы либо поправить файл,
            // либо отдельно мигрировать сделку в нужный проект.
            string projectClause = (otherProjectId, otherProjectTitle) switch
            {
                (int id, string t) when !string.IsNullOrWhiteSpace(t)
                    => $"проектом «{t}» (ID={id})",
                (int id, _) => $"проектом ID={id}",
                _           => "другим проектом",
            };
            _log.LogWarning(
                "FinModelImportMapper: deal exists globally but belongs to other project — dealId={DealId}, otherProjectId={OtherProjectId}",
                globalMatch.ID, otherProjectId);
            foreach (var pr in paramRows)
            {
                errors.Add(new RowError(
                    "Номер договора", "deal_in_other_project",
                    $"Сделка (№ «{docNumber}») связана с {projectClause}. Импорт параметров пропущен.",
                    pr.SourceRowNumber, pr.Sheet));
            }
            return false;
        }

        // Сделка не найдена ни в проекте, ни глобально — создаём её сами в проекте.
        // ⚠️ Title="-" — временный костыль. Заказчик подтвердил, что Visary сейчас требует
        // непустой Title (иначе 400), но в будущем требование уйдёт. Когда сервер начнёт
        // принимать null/отсутствующий Title — удалить из payload здесь И поле Title из
        // DealCreateRequest. См. memory entry project_finmodel_deal_create_title_hack.
        // LmID не передаём (v1.3) — только DocNumber.
        _log.LogInformation(
            "FinModelImportMapper: deal not found in projectId={ProjectId} (DocNumber='{DocNumber}') — создаём",
            projectId.Value, docNumber);
        try
        {
            var created = await _visaryClient.CreateDealAsync(new DealCreateRequest
            {
                ConstructionProjectID = projectId.Value,
                ConstructionProject   = new VisaryRef { ID = projectId.Value },
                DocNumber             = docNumber,
                Title                 = "-", // TODO: удалить, когда Visary перестанет требовать Title
            }, ct);
            rowActions.Add(new RowActionLog(
                firstRow.SourceRowNumber, firstRow.Sheet,
                new[] { $"Сделка создана в проекте: ID={created.ID}, № «{docNumber}»." }));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper: deal create failed for projectId={ProjectId} docNumber='{DocNumber}'",
                projectId.Value, docNumber);
            foreach (var pr in paramRows)
            {
                errors.Add(new RowError(
                    "Номер договора", "deal_create_error",
                    $"Не удалось создать сделку в проекте (№ «{docNumber}»): {ex.Message}",
                    pr.SourceRowNumber, pr.Sheet));
            }
            return false;
        }
    }

    // ─── Organization / ProjectManagement flow ───────────────────────────────
    //
    // Раздел «Основные данные» из шаблона Финмодели содержит ИНН + наименование
    // организации-Заёмщика/Застройщика. По аналогии с импортом Помещений (doc 75)
    // алгоритм:
    //   1. Найти Organization в Visary по ClientID=ИНН (listview/organization).
    //      Если нет — создать через POST /crud/organization (Title, ClientID).
    //   2. Проверить, есть ли уже projectmanagement-запись на этом сайте,
    //      связанная с найденной/созданной Organization (любая роль). Если есть —
    //      flow завершён (организация уже видна в «Участниках Объекта»).
    //   3. Иначе — посмотреть PM в рамках проекта (onetomany/Project) с этой
    //      Organization. Если есть подходящая запись — переиспользуем (max ID).
    //      Если нет — создаём новую PM (Role=«Застройщик» по умолчанию).
    //   4. Привязываем PM к сайту через manytomany/link.
    //
    // Решение: при поиске PM в проекте мы НЕ фильтруем по Role.ID, потому что
    // одна организация может присутствовать в разных ролях (Застройщик/Заёмщик),
    // и нам подходит любая существующая. При создании используем Role=Developer (10);
    // справочник ролей Visary целиком пока не интегрирован.
    private async Task<int> LinkBorrowerOrganizationAsync(
        int siteId, string inn, string borrowerTitle, CancellationToken ct)
    {
        // (1) Organization по ClientID=ИНН.
        var orgs = await _listViewClient.GetOrganizationsByClientIdAsync(inn, ct);
        // Visary возвращает несколько записей при «contains»-семантике поиска,
        // поэтому фильтруем локально по точному совпадению ClientID (Trim).
        var existingOrg = orgs.Data.FirstOrDefault(o =>
            string.Equals(o.ClientID?.Trim(), inn.Trim(), StringComparison.Ordinal));

        int orgId;
        if (existingOrg is not null)
        {
            orgId = existingOrg.ID;
            _log.LogInformation(
                "FinModelImportMapper: organization '{Title}' (ID={OrgId}) found by INN={Inn}",
                existingOrg.Title, orgId, inn);
        }
        else
        {
            var created = await _visaryClient.CreateOrganizationAsync(new OrganizationCreateRequest
            {
                Title = borrowerTitle,
                ClientID = inn,
                INN = inn,
            }, ct);
            orgId = created.ID;
            _log.LogInformation(
                "FinModelImportMapper: organization '{Title}' (INN={Inn}) created with ID={OrgId}",
                borrowerTitle, inn, orgId);
        }

        // (2) Уже привязана к сайту?
        var siteSPm = await _listViewClient.GetProjectManagementsBySiteAsync(siteId, ct);
        if (siteSPm.Data.Any(pm => pm.Organization?.ID == orgId))
        {
            _log.LogInformation(
                "FinModelImportMapper: organization ID={OrgId} already linked to siteId={SiteId} — skip PM",
                orgId, siteId);
            return orgId;
        }

        // (3) Найти/создать PM в проекте.
        var siteFull = await _visaryClient.GetSiteByIdFullAsync(siteId, ct);
        var projectId = siteFull.Project?.ID;
        if (projectId is null)
        {
            throw new InvalidOperationException(
                $"У объекта siteId={siteId} не задан Project — невозможно создать projectmanagement-запись.");
        }

        // Без фильтра по Role.ID — берём любую существующую PM этой Organization
        // в проекте (Застройщик/Заёмщик/любая) и переиспользуем.
        var inProject = await _listViewClient.GetProjectManagementsByProjectAsync(
            projectId.Value, orgId, roleId: null, ct);
        var reusable = inProject.Data
            .Where(pm => pm.Organization?.ID == orgId)
            .OrderByDescending(pm => pm.ID)
            .FirstOrDefault();

        int pmIdToLink;
        if (reusable is not null)
        {
            pmIdToLink = reusable.ID;
            _log.LogInformation(
                "FinModelImportMapper: reusing projectmanagement ID={PmId} (orgId={OrgId}, roleId={RoleId}) in projectId={ProjectId}",
                pmIdToLink, orgId, reusable.Role?.ID, projectId);
        }
        else
        {
            var createdPm = await _visaryClient.CreateProjectManagementAsync(new ProjectManagementCreateRequest
            {
                Project = new VisaryRef { ID = projectId.Value },
                Organization = new VisaryRef { ID = orgId },
                Role = new VisaryRef
                {
                    ID = ProjectManagementRoles.Developer,
                    Title = ProjectManagementRoles.DeveloperTitle,
                },
                Affiliation = 0,
            }, ct);
            pmIdToLink = createdPm.ID;
            _log.LogInformation(
                "FinModelImportMapper: created projectmanagement ID={PmId} (orgId={OrgId}, role=Застройщик) in projectId={ProjectId}",
                pmIdToLink, orgId, projectId);
        }

        // (4) Linkage PM ↔ Site.
        await _visaryClient.LinkProjectManagementToSiteAsync(siteId, pmIdToLink, ct);
        return orgId;
    }

    // ─── CompanyGroup (привязка организации к материнской ГК) ────────────────
    //
    // По doc 100: после того как организация-застройщик найдена/создана и
    // привязана к проекту (LinkBorrowerOrganizationAsync), пытаемся проставить
    // у неё поле Group (group of companies). Алгоритм:
    //   ① GET /crud/organization/{orgId} → если Group уже задана → row-action «skip»;
    //   ② POST /listview/companygroup Filter ["Title","=",title] → если ровно одна
    //      запись → ③ PATCH /crud/organization/{orgId} с Group:{ID,Title,Hidden:false};
    //   • 0 записей или >1 — row-error «ГК не найдена, тк {причина}», шаг продолжается
    //     (мы не отменяем уже применённые FK/Address/Org-link/Indicators).
    //
    // Метод НЕ бросает исключений на «бизнес-ошибки» — все 4 исхода (skip / linked /
    // not-found / multiple-found) выражены через errors-список. Технические сбои
    // (HTTP 5xx и т.п.) пробрасываются — caller их ловит и оформляет как
    // company_group_link_error.
    private async Task LinkCompanyGroupAsync(
        int orgId,
        string companyGroupTitle,
        List<RowError> errors,
        Func<string, string, string?, RowError> rowErrorFactory,
        CancellationToken ct)
    {
        // (1) Текущее состояние организации (Group + RowVersion).
        var orgFull = await _visaryClient.GetOrganizationByIdAsync(orgId, ct);
        if (orgFull.Group is { ID: var existingGroupId, Title: var existingTitle })
        {
            _log.LogInformation(
                "FinModelImportMapper: orgId={OrgId} already has Group ID={GroupId} title='{Title}' — skip",
                orgId, existingGroupId, existingTitle);
            return; // успех — но без вызова, без ошибки. Идемпотентность.
        }

        // (2) Поиск ГК по точному Title.
        var groups = await _listViewClient.GetCompanyGroupsByTitleAsync(companyGroupTitle, ct);
        // Visary иногда матчит с лишними пробелами — фильтруем локально по Trim+OrdinalIgnoreCase.
        var needle = companyGroupTitle.Trim();
        var matches = groups.Data
            .Where(g => string.Equals(g.Title?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            errors.Add(rowErrorFactory("company_group_not_found",
                $"ГК не найдена, тк в Visary нет записи companygroup с Title='{companyGroupTitle}'.",
                null));
            _log.LogInformation(
                "FinModelImportMapper: companygroup with Title='{Title}' not found — row-error, continue",
                companyGroupTitle);
            return;
        }

        if (matches.Count > 1)
        {
            var ids = string.Join(", ", matches.Select(g => g.ID));
            errors.Add(rowErrorFactory("company_group_multiple_found",
                $"ГК не найдена, тк в Visary найдено несколько записей companygroup с Title='{companyGroupTitle}' (ID: {ids}). Однозначно сопоставить нельзя.",
                null));
            _log.LogInformation(
                "FinModelImportMapper: companygroup with Title='{Title}' returned {N} matches — row-error, continue",
                companyGroupTitle, matches.Count);
            return;
        }

        var group = matches[0];
        // (3) PATCH /crud/organization/{orgId} с Group.
        await _visaryClient.UpdateOrganizationGroupAsync(orgId, group.ID, group.Title ?? companyGroupTitle, ct);
        _log.LogInformation(
            "FinModelImportMapper: orgId={OrgId} linked to companygroup ID={GroupId} title='{Title}'",
            orgId, group.ID, group.Title);
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

    // ─── Budget flow ─────────────────────────────────────────────────────────

    /// <summary>
    /// Сборка mapped-строк бюджета: проходим бюджетные ParsedRow в порядке исходных строк,
    /// отслеживаем текущую главу через эталонный справочник, агрегируем суммы (по этапам)
    /// одной и той же подстатьи. На выходе — по одному <see cref="MappedRow"/> на
    /// уникальную пару (ChapterCode, ArticleCode) с готовым <c>DeclaredSum</c>/<c>ConfirmedSum</c>.
    ///
    /// Title из файла резолвится в эталонную запись через
    /// <see cref="IBudgetReferenceProvider.FindByTitle"/> (нормализация: lower-case +
    /// схлопывание пробелов/переносов). Не найденные Title — мягкий skip с trace-логом
    /// (валидное поведение для v0.2: в реальном файле много рабочих заголовков и сумм,
    /// которые не имеют соответствия в справочнике).
    /// </summary>
    private List<MappedRow> ValidateBudget(
        IReadOnlyList<ParsedRow> budgetRows, List<RowError> fileErrors)
    {
        var mapped = new List<MappedRow>();
        if (budgetRows.Count == 0) return mapped;

        // Сортируем по SourceRowNumber, чтобы chapter-tracking шёл сверху вниз.
        var ordered = budgetRows.OrderBy(r => r.SourceRowNumber).ToList();
        // Бюджетные строки приходят из ОДНОГО листа (KeyValueVertical с BudgetSectionHint
        // эмитит их с одинаковым `Sheet = "{sheetName} {SheetMarker}"`). Берём имя из
        // первой строки и проставляем во все агрегированные MappedRow — иначе пайплайн
        // запишет StagedRow.Sheet="" и сломает (Sheet, SourceRowNumber) инвариант.
        var budgetSheet = ordered[0].Sheet ?? string.Empty;

        BudgetReferenceEntry? currentChapter = null;
        // chapterClosed = true после строки «Итого…» текущей главы. В файле финмодели
        // после «Итого» обычно идут «фактические» / следующий «Этап» с теми же названиями
        // статей — их не суммируем (двойной учёт). Сбрасываем на следующей «Глава X».
        // Раньше всё после «Итого» аккумулировалось → 1.8 вместо 2 222 получал 152 222 (см. ТЗ от 2026-05-14).
        bool chapterClosed = false;
        // Бакет (chapterCode + articleCode) → суммарный (Sum, ArticleEntry, FirstRowNumber).
        var aggregated = new Dictionary<string, BudgetAggregateBucket>(StringComparer.Ordinal);
        // Прямой ИТОГО главы из файла (Code → Sum, RowNumber). Override для агрегата
        // в exporter-е: в файле статьи Глав 2/3 не совпадают со справочником («Стоимость
        // СМР», «Инфляционное удорожание» и т.п.), поэтому сумма Главы агрегацией из
        // children получится 0. Берём её из строки «Итого» главы напрямую. См. ТЗ 2026-05-14.
        var chapterDirectTotals = new Dictionary<string, ChapterTotalBucket>(StringComparer.Ordinal);
        int matchedRows = 0, unmatchedRows = 0;

        foreach (var row in ordered)
        {
            if (!row.Cells.TryGetValue("C", out var rawTitle)) continue;
            var title = rawTitle?.Trim();
            if (string.IsNullOrEmpty(title)) continue;

            // «Итого…» — конец сборки данных для currentChapter; всё, что ниже до новой
            // «Глава X», игнорируем (повторы статей под «Этап 2», «фактические» и т.п.).
            // Дополнительно: фиксируем ИТОГО главы как chapter-direct сумму.
            if (title.StartsWith("Итого", StringComparison.OrdinalIgnoreCase))
            {
                if (currentChapter is not null && !chapterClosed)
                {
                    row.Cells.TryGetValue("E", out var totalStr);
                    var chapterTotal = ParseSumOrZero(totalStr);
                    if (chapterTotal > 0
                        && !chapterDirectTotals.ContainsKey(currentChapter.Code))
                    {
                        chapterDirectTotals[currentChapter.Code] = new ChapterTotalBucket(
                            Chapter: currentChapter, Sum: chapterTotal, RowNumber: row.SourceRowNumber);
                    }
                    chapterClosed = true;
                }
                continue;
            }
            // «Этап 1»/«Этапы» — служебные подписи, не данные.
            if (title.StartsWith("Этап", StringComparison.OrdinalIgnoreCase)) continue;

            var entry = _budgetRef.FindByTitle(title);

            // Fallback 1: главу матчим по «Глава N» префиксу, если полный заголовок
            // не совпал. В файле финмодели часто другие суффиксы: «Глава 2. Стоимость СМР»
            // (файл) vs «Глава 2. Стоимость строительства» (справочник). Идентификатор —
            // номер главы, не описательная часть.
            if (entry is null)
            {
                entry = FindChapterByPrefix(title);
                if (entry is not null)
                {
                    _log.LogDebug(
                        "Budget row {RowNum}: '{Title}' resolved via chapter-prefix → {Code} '{RefTitle}'",
                        row.SourceRowNumber, title, entry.Code, entry.Title);
                }
            }

            // Fallback 2: глобальный FindByTitle не нашёл — это часто бывает, когда в файле
            // используется КОРОТКАЯ форма Title, а в справочнике — длинная. Пример:
            // файл «Прочие затраты» ↔ справочник «Прочие затраты на улучшения и содержание ЗУ» (1.8).
            // Глобально добавлять reverse-prefix в FindByTitle опасно (одно и то же
            // «Прочие …» может матчить разные подстатьи в разных главах), а с известной
            // currentChapter — уже однозначно. Ищем среди потомков текущей главы.
            if (entry is null && currentChapter is not null)
            {
                entry = FindArticleInChapterByPrefix(title, currentChapter);
                if (entry is not null)
                {
                    _log.LogDebug(
                        "Budget row {RowNum}: '{Title}' resolved via reverse-prefix in chapter '{Chapter}' → {Code} '{RefTitle}'",
                        row.SourceRowNumber, title, currentChapter.Code, entry.Code, entry.Title);
                }
            }

            if (entry is null)
            {
                unmatchedRows++;
                _log.LogTrace(
                    "Budget row {RowNum}: Title '{Title}' не найден в справочнике — skip",
                    row.SourceRowNumber, title);
                continue;
            }

            if (entry.IsChapter)
            {
                currentChapter = entry;
                chapterClosed = false; // новая глава — открываем сбор данных
                continue;
            }

            // Если текущая глава уже закрыта (видели её «Итого»), повторные article-строки
            // ниже относятся к другому представлению (Этап 2, фактические и т.д.) — не
            // дублируем их в агрегат.
            if (chapterClosed) continue;

            // Article: parse sum from column E (если её нет — 0).
            row.Cells.TryGetValue("E", out var sumStr);
            var sum = ParseSumOrZero(sumStr);

            // ParentCode у статьи — Code главы (или промежуточной секции). Если у нас
            // currentChapter не выставлен (файл начался с подстатьи) — берём parent из
            // самого entry; если parent — не глава, поднимаемся до главы по справочнику.
            var chapter = currentChapter
                          ?? ResolveChapterFor(entry)
                          ?? null;
            if (chapter is null)
            {
                _log.LogTrace(
                    "Budget row {RowNum}: невозможно определить главу для '{Title}' (Code={Code})",
                    row.SourceRowNumber, entry.Title, entry.Code);
                unmatchedRows++;
                continue;
            }

            matchedRows++;
            var key = $"{chapter.Code}|{entry.Code}";
            if (aggregated.TryGetValue(key, out var bucket))
            {
                bucket.Sum += sum;
            }
            else
            {
                aggregated[key] = new BudgetAggregateBucket(
                    Chapter: chapter, Article: entry,
                    Sum: sum, FirstRowNumber: row.SourceRowNumber);
            }
        }

        if (aggregated.Count == 0 && matchedRows == 0 && chapterDirectTotals.Count == 0)
        {
            _log.LogInformation(
                "FinModelImportMapper: budget block scanned ({BudgetRows} rows), но ни одной подстатьи не сопоставлено со справочником.",
                budgetRows.Count);
            return mapped;
        }

        foreach (var bucket in aggregated.Values.OrderBy(b => b.FirstRowNumber))
        {
            var json = JsonSerializer.Serialize(new
            {
                Kind          = "budget",
                ChapterCode   = bucket.Chapter.Code,
                ChapterTitle  = bucket.Chapter.Title,
                ArticleCode   = bucket.Article.Code,
                ArticleTitle  = bucket.Article.Title,
                DeclaredSum   = bucket.Sum,
                ConfirmedSum  = bucket.Sum,
            });
            mapped.Add(new MappedRow(
                bucket.FirstRowNumber, budgetSheet, true, JsonDocument.Parse(json), Array.Empty<RowError>()));
        }

        // Эмитим chapter-direct итоги отдельным набором строк (ArticleCode == ChapterCode):
        // exporter их распознаёт и переписывает агрегированную сумму главы.
        foreach (var total in chapterDirectTotals.Values.OrderBy(t => t.RowNumber))
        {
            var json = JsonSerializer.Serialize(new
            {
                Kind          = "budget",
                ChapterCode   = total.Chapter.Code,
                ChapterTitle  = total.Chapter.Title,
                ArticleCode   = total.Chapter.Code,   // sentinel: == ChapterCode = «это ИТОГО главы»
                ArticleTitle  = total.Chapter.Title,
                DeclaredSum   = total.Sum,
                ConfirmedSum  = total.Sum,
            });
            mapped.Add(new MappedRow(
                total.RowNumber, budgetSheet, true, JsonDocument.Parse(json), Array.Empty<RowError>()));
        }

        _log.LogInformation(
            "FinModelImportMapper: budget aggregated → {Articles} уникальных подстатей + {ChapterTotals} chapter-direct итогов (матчей в справочнике: {Matched}, пропущено: {Skipped})",
            aggregated.Count, chapterDirectTotals.Count, matchedRows, unmatchedRows);
        return mapped;
    }

    private sealed record ChapterTotalBucket(BudgetReferenceEntry Chapter, double Sum, int RowNumber);

    /// <summary>
    /// Регэкс «Глава N» в начале строки — число главы извлекается в группу 1.
    /// Терпим к пробелам / точке после номера: «Глава 2», «Глава 2.», «  Глава  2.  ».
    /// </summary>
    private static readonly Regex ChapterPrefixRegex = new(
        @"^\s*Глава\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Резолвит главу справочника по префиксу «Глава N» (без учёта описательного суффикса).
    /// Нужно потому, что в файле финмодели заголовок главы может отличаться от справочного:
    /// файл «Глава 2. Стоимость СМР» ↔ справочник «Глава 2. Стоимость строительства».
    /// Идентификатор главы — её номер (Code), не Title.
    /// </summary>
    private BudgetReferenceEntry? FindChapterByPrefix(string title)
    {
        var m = ChapterPrefixRegex.Match(title);
        if (!m.Success) return null;
        var code = m.Groups[1].Value + ".";
        var entry = _budgetRef.FindByCode(code);
        return entry is { IsChapter: true } ? entry : null;
    }

    /// <summary>
    /// Reverse-prefix матч в пределах главы: ищет среди потомков <paramref name="chapter"/>
    /// (не-главы) запись, у которой Title начинается с переданного <paramref name="title"/>
    /// (после нормализации). Используется когда файл финмодели даёт короткую форму
    /// названия, а справочник — полную (пример: «Прочие затраты» ↔ «Прочие затраты на
    /// улучшения и содержание ЗУ»). Граница слова за prefix-ом — пробел/запятая/точка/скобка,
    /// чтобы не зацепить случайное префиксное совпадение «Затраты на» → любая «Затраты на…».
    ///
    /// Если кандидатов несколько — берётся самый КОРОТКИЙ Title (он ближе к prefix-у,
    /// меньше «лишнего» хвоста). Если конкурентов на одну длину — возвращается null
    /// (не угадываем при двусмысленности).
    /// </summary>
    private BudgetReferenceEntry? FindArticleInChapterByPrefix(string title, BudgetReferenceEntry chapter)
    {
        var key = BudgetReferenceEntry.NormalizeTitle(title);
        if (string.IsNullOrEmpty(key)) return null;

        BudgetReferenceEntry? best = null;
        bool ambiguous = false;
        foreach (var e in _budgetRef.Entries)
        {
            if (e.IsChapter) continue;
            if (!IsDescendantOf(e, chapter)) continue;

            var refKey = e.NormalizedTitle;
            if (refKey.Length <= key.Length) continue;
            if (!refKey.StartsWith(key, StringComparison.Ordinal)) continue;
            var boundary = refKey[key.Length];
            if (char.IsLetterOrDigit(boundary)) continue;

            if (best is null || refKey.Length < best.NormalizedTitle.Length)
            {
                best = e;
                ambiguous = false;
            }
            else if (refKey.Length == best.NormalizedTitle.Length)
            {
                ambiguous = true;
            }
        }
        return ambiguous ? null : best;
    }

    private bool IsDescendantOf(BudgetReferenceEntry entry, BudgetReferenceEntry chapter)
    {
        var cursor = entry.ParentCode;
        while (cursor is not null)
        {
            if (string.Equals(cursor, chapter.Code, StringComparison.Ordinal)) return true;
            var parent = _budgetRef.FindByCode(cursor);
            if (parent is null) return false;
            cursor = parent.ParentCode;
        }
        return false;
    }

    private BudgetReferenceEntry? ResolveChapterFor(BudgetReferenceEntry entry)
    {
        // Поднимаемся по ParentCode до главы (Depth=1).
        var cursor = entry;
        while (cursor.ParentCode is not null)
        {
            var parent = _budgetRef.FindByCode(cursor.ParentCode);
            if (parent is null) return null;
            if (parent.IsChapter) return parent;
            cursor = parent;
        }
        return cursor.IsChapter ? cursor : null;
    }

    private async Task<int?> ResolveProjectIdAsync(
        int siteId, int? contextProjectId, VisaryDbContext visaryDb, CancellationToken ct)
    {
        if (contextProjectId is > 0) return contextProjectId;

        // Берём ProjectID из локального Visary-зеркала (синкается отдельно).
        // При невалидном/устаревшем зеркале — null, ошибка пробросится выше.
        var site = await visaryDb.ConstructionSites
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == siteId, ct);
        return site?.ConstructionProjectId is > 0 ? site.ConstructionProjectId : null;
    }

    /// <summary>
    /// Идемпотентное применение бюджета: для каждой главы-уникальной выгружаем
    /// существующие WBS проекта, ищем главу/подстатью по Title; если статья есть —
    /// PATCH-аем суммы, если нет — создаём. Подстатьи привязываем к ОКСу.
    /// </summary>
    private async Task<int> ApplyBudgetAsync(
        int projectId, int siteId,
        IReadOnlyList<MappedRow> budgetRows, List<RowError> errors, CancellationToken ct)
    {
        // 1) Один раз тянем существующий WBS-список проекта — для матчинга глав/статей.
        ListViewResponse<WbsRaw> existing;
        try
        {
            existing = await _listViewClient.GetWbsByProjectAsync(projectId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Budget: failed to load existing WBS for projectId={Pid}", projectId);
            errors.Add(new RowError(null, "wbs_list_failed",
                $"Не удалось получить существующий WBS проекта {projectId}: {ex.Message}"));
            return 0;
        }

        // Группируем mapped по ChapterCode (порядок важен для логов: Глава 1 → 2 → …).
        var byChapter = budgetRows
            .GroupBy(r => r.MappedValues.RootElement.GetProperty("ChapterCode").GetString()!)
            .OrderBy(g => g.Key);

        // Кэш chapter-id, чтобы не дёргать find дважды при нескольких подстатьях главы.
        var chapterIdByTitle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int created = 0, patched = 0, failures = 0;

        foreach (var group in byChapter)
        {
            ct.ThrowIfCancellationRequested();
            var first = group.First().MappedValues.RootElement;
            var chapterTitle = first.GetProperty("ChapterTitle").GetString()!;
            var chapterCode = first.GetProperty("ChapterCode").GetString()!;

            // 2) Глава: сначала ищем в existing.Data по Title (ParentID is null).
            int chapterId;
            try
            {
                chapterId = await EnsureChapterAsync(
                    projectId, chapterTitle, chapterCode, existing.Data, chapterIdByTitle, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Budget: failed to ensure chapter '{Title}'", chapterTitle);
                errors.Add(new RowError(null, "wbs_chapter_failed",
                    $"Не удалось создать/найти главу '{chapterTitle}': {ex.Message}"));
                failures++;
                continue;
            }

            // 3) Подстатьи: одна за другой, идемпотентно.
            foreach (var row in group)
            {
                ct.ThrowIfCancellationRequested();
                var root = row.MappedValues.RootElement;
                var articleTitle = root.GetProperty("ArticleTitle").GetString()!;
                var declared     = root.GetProperty("DeclaredSum").GetDouble();
                var confirmed    = root.GetProperty("ConfirmedSum").GetDouble();

                try
                {
                    var (op, _) = await UpsertArticleAsync(
                        projectId, siteId, chapterId,
                        articleTitle, declared, confirmed,
                        existing.Data, ct);
                    if (op == BudgetOp.Created) created++;
                    else if (op == BudgetOp.Patched) patched++;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Budget: failed to upsert article '{Title}'", articleTitle);
                    errors.Add(new RowError(null, "wbs_article_failed",
                        $"Не удалось импортировать статью '{articleTitle}' (глава '{chapterTitle}'): {ex.Message}"));
                    failures++;
                }
            }
        }

        _log.LogInformation(
            "FinModelImportMapper.ApplyBudgetAsync: created={Created} patched={Patched} failed={Failed} (siteId={SiteId} projectId={Pid})",
            created, patched, failures, siteId, projectId);
        return created + patched;
    }

    private async Task<int> EnsureChapterAsync(
        int projectId, string chapterTitle, string chapterCode,
        IReadOnlyList<WbsRaw> existing, Dictionary<string, int> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(chapterTitle, out var cachedId)) return cachedId;

        var match = FindChapter(existing, chapterTitle, chapterCode);
        if (match is not null)
        {
            cache[chapterTitle] = match.ID;
            _log.LogDebug("Budget: chapter '{Title}' уже существует — id={Id} code={Code}",
                chapterTitle, match.ID, match.Code);
            return match.ID;
        }

        // Создаём главу: ParentID/Parent = null (top-level), ConstructionSite не привязан
        // (главу проекта обычно держат «общей»; подстатьи привяжем к ОКСу).
        var created = await _visaryClient.CreateWbsAsync(new WbsCreateRequest
        {
            ProjectID = projectId,
            Project = new VisaryRef { ID = projectId },
            Title = chapterTitle,
            ParentID = null,
            Parent = null,
        }, ct);
        cache[chapterTitle] = created.ID;
        _log.LogInformation("Budget: chapter '{Title}' created → id={Id} code={Code}",
            chapterTitle, created.ID, created.Code);
        return created.ID;
    }

    private static WbsRaw? FindChapter(IReadOnlyList<WbsRaw> wbs, string title, string code)
    {
        var titleNorm = BudgetReferenceEntry.NormalizeTitle(title);
        // Сначала ищем главу по Code (точному, заданному сервером — например "1.").
        var byCode = wbs.FirstOrDefault(w =>
            w.ParentID is null
            && string.Equals(w.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase));
        if (byCode is not null) return byCode;
        // Если код ещё не присвоен (только что создали в параллельной сессии — маловероятно)
        // — fallback по Title.
        return wbs.FirstOrDefault(w =>
            w.ParentID is null
            && BudgetReferenceEntry.NormalizeTitle(w.Title ?? "") == titleNorm);
    }

    private async Task<(BudgetOp Op, int WbsId)> UpsertArticleAsync(
        int projectId, int siteId, int chapterId,
        string articleTitle, double declaredSum, double confirmedSum,
        IReadOnlyList<WbsRaw> existing, CancellationToken ct)
    {
        // Идемпотентность: ищем существующую подстатью под этой главой по Title.
        var titleNorm = BudgetReferenceEntry.NormalizeTitle(articleTitle);
        var match = existing.FirstOrDefault(w =>
            w.ParentID == chapterId
            && BudgetReferenceEntry.NormalizeTitle(w.Title ?? "") == titleNorm);

        if (match is not null)
        {
            // Если суммы уже совпадают — пропускаем, чтобы не было «фантомных» PATCH.
            if (NearlyEqual(match.DeclaredSum, declaredSum)
                && NearlyEqual(match.ConfirmedSum, confirmedSum))
            {
                _log.LogDebug(
                    "Budget: article '{Title}' (id={Id}) — суммы совпадают ({Sum}), PATCH не нужен",
                    articleTitle, match.ID, declaredSum);
                return (BudgetOp.Skipped, match.ID);
            }

            await _visaryClient.PatchWbsAsync(match.ID, new WbsPatchRequest
            {
                DeclaredSum = declaredSum,
                ConfirmedSum = confirmedSum,
            }, ct);
            _log.LogInformation(
                "Budget: article '{Title}' (id={Id}) PATCH сумм {OldDeclared}→{NewDeclared}",
                articleTitle, match.ID,
                match.DeclaredSum?.ToString(CultureInfo.InvariantCulture) ?? "null", declaredSum);
            return (BudgetOp.Patched, match.ID);
        }

        var created = await _visaryClient.CreateWbsAsync(new WbsCreateRequest
        {
            ProjectID = projectId,
            Project = new VisaryRef { ID = projectId },
            ParentID = chapterId,
            Parent = new VisaryRef { ID = chapterId },
            ConstructionSiteID = siteId,
            ConstructionSite = new VisaryRef { ID = siteId },
            Title = articleTitle,
            DeclaredSum = declaredSum,
            ConfirmedSum = confirmedSum,
        }, ct);
        _log.LogInformation(
            "Budget: article '{Title}' created → id={Id} code={Code} sum={Sum}",
            articleTitle, created.ID, created.Code, declaredSum);
        return (BudgetOp.Created, created.ID);
    }

    private static bool NearlyEqual(double? a, double b)
    {
        if (a is null) return false;
        return Math.Abs(a.Value - b) < 0.005; // в Visary суммы хранятся с 2 знаками
    }

    private static double ParseSumOrZero(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        return TryParseFlexibleDouble(raw, out var d) ? d : 0;
    }

    private static bool IsBudgetRow(ParsedRow row)
        => row.Sheet?.EndsWith(BudgetSheetSuffix, StringComparison.Ordinal) == true;

    private static bool IsScheduleRow(ParsedRow row)
        => row.Sheet?.EndsWith(ScheduleSheetSuffix, StringComparison.Ordinal) == true;

    private static string GetKind(MappedRow r)
    {
        var root = r.MappedValues.RootElement;
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty("Kind", out var k)
               && k.ValueKind == JsonValueKind.String
            ? (k.GetString() ?? "params")
            : "params";
    }

    private enum BudgetOp { Created, Patched, Skipped }

    private sealed class BudgetAggregateBucket(
        BudgetReferenceEntry Chapter,
        BudgetReferenceEntry Article,
        double Sum,
        int FirstRowNumber)
    {
        public BudgetReferenceEntry Chapter { get; } = Chapter;
        public BudgetReferenceEntry Article { get; } = Article;
        public double Sum { get; set; } = Sum;
        public int FirstRowNumber { get; } = FirstRowNumber;
    }

    // ─── Chapter 1 Schedule (ГФ) flow ────────────────────────────────────────

    /// <summary>
    /// Сборка mapped-строк ГФ Главы 1 из schedule-секции парсера:
    /// <list type="bullet">
    /// <item>Одна <see cref="MappedRow"/> с <c>Kind="schedule_quarters"</c> — словарь
    /// <c>{ ColumnLetter → DateTime начала квартала }</c>. Берётся из header-row
    /// (sentinel <see cref="Parsers.XlsxParser.ChapterScheduleQuartersSentinel"/>
    /// в колонке C).</item>
    /// <item>По одной <see cref="MappedRow"/> с <c>Kind="schedule_article"</c> на каждую
    /// matched-статью Этапа 1 (Title → Code через <see cref="IBudgetReferenceProvider"/>
    /// + явный <see cref="Chapter1TitleAliases"/>). В <c>MappedValues</c>: ArticleCode,
    /// ArticleTitle, ChapterCode="1.", SourceRowNumber (= Excel-строка статьи),
    /// Quarters: [{ ColLetter, AmountThousands }] (только непустые ячейки).</item>
    /// </list>
    /// Этап 2/3 игнорируем (по решению пользователя от 2026-05-19, п.1) — берём блок
    /// от «Этап 1» до следующего «Этап»/«Итого».
    /// </summary>
    private List<MappedRow> ValidateChapter1Schedule(
        IReadOnlyList<ParsedRow> scheduleRows, List<RowError> fileErrors)
    {
        var mapped = new List<MappedRow>();
        if (scheduleRows.Count == 0) return mapped;

        var ordered = scheduleRows.OrderBy(r => r.SourceRowNumber).ToList();
        var scheduleSheet = ordered[0].Sheet ?? string.Empty;

        // 1) Найти датовую строку (sentinel "__quarters__" в колонке C).
        var headerRow = ordered.FirstOrDefault(r =>
            r.Cells.TryGetValue("C", out var c)
            && string.Equals(c, XlsxParser.ChapterScheduleQuartersSentinel, StringComparison.Ordinal));
        if (headerRow is null)
        {
            _log.LogInformation(
                "FinModelImportMapper: schedule-секция без header-строки — ГФ пропущен. Парсер передал {Count} строк.",
                ordered.Count);
            return mapped;
        }

        // Собираем словарь {ColLetter → ISO-дата}; пустые / некорректные ячейки молча
        // игнорируем (за CU могут быть пустые колонки в шаблоне).
        var quartersJson = new List<object>();
        var quartersByLetter = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var kv in headerRow.Cells)
        {
            if (string.Equals(kv.Key, "C", StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (!DateTime.TryParseExact(kv.Value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                continue;
            }
            quartersByLetter[kv.Key] = dt;
            quartersJson.Add(new { Col = kv.Key, Date = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        }
        if (quartersByLetter.Count == 0)
        {
            _log.LogWarning("FinModelImportMapper: schedule header-row без распарсенных дат — ГФ пропущен.");
            return mapped;
        }

        var quartersJsonStr = JsonSerializer.Serialize(new { Kind = "schedule_quarters", Quarters = quartersJson });
        mapped.Add(new MappedRow(
            headerRow.SourceRowNumber, scheduleSheet, true,
            JsonDocument.Parse(quartersJsonStr), Array.Empty<RowError>()));

        // 2) Найти «Этап 1» и собрать статьи до следующего «Этап»/«Итого».
        var articleRows = ordered
            .Where(r => r.SourceRowNumber != headerRow.SourceRowNumber)
            .ToList();

        int? stage1StartIdx = null;
        for (int i = 0; i < articleRows.Count; i++)
        {
            var title = articleRows[i].Cells.GetValueOrDefault("C")?.Trim();
            if (string.IsNullOrEmpty(title)) continue;
            if (title.StartsWith(Chapter1Stage1Marker, StringComparison.OrdinalIgnoreCase))
            {
                stage1StartIdx = i + 1;
                break;
            }
        }
        if (stage1StartIdx is null)
        {
            _log.LogInformation(
                "FinModelImportMapper: в ГФ-секции не найден маркер '{Marker}' — пропускаем (возможно файл без квартальной таблицы Главы 1).",
                Chapter1Stage1Marker);
            return mapped;
        }

        int matched = 0, skippedUnknown = 0;
        for (int i = stage1StartIdx.Value; i < articleRows.Count; i++)
        {
            var row = articleRows[i];
            var title = row.Cells.GetValueOrDefault("C")?.Trim();
            if (string.IsNullOrEmpty(title)) continue;

            // Стоп: следующий «Этап» / «Итого» / любой другой Глава — окончание Этапа 1.
            if (title.StartsWith(Chapter1StageMarkerPrefix, StringComparison.OrdinalIgnoreCase)
                || title.StartsWith(Chapter1TotalMarkerPrefix, StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("Глава", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // Resolve code: alias → provider.FindByTitle.
            string? code = null;
            string? matchedRefTitle = null;
            if (Chapter1TitleAliases.TryGetValue(title, out var aliasCode))
            {
                code = aliasCode;
                matchedRefTitle = _budgetRef.FindByCode(aliasCode)?.Title;
            }
            else
            {
                var entry = _budgetRef.FindByTitle(title);
                // Принимаем только статьи Главы 1 (Code starts with "1.").
                if (entry is not null
                    && entry.Code.StartsWith("1.", StringComparison.Ordinal)
                    && !entry.IsChapter)
                {
                    code = entry.Code;
                    matchedRefTitle = entry.Title;
                }
            }

            if (code is null)
            {
                skippedUnknown++;
                _log.LogTrace(
                    "Schedule row {RowNum}: Title '{Title}' не сопоставлен со статьёй Главы 1 — skip",
                    row.SourceRowNumber, title);
                continue;
            }

            // Собираем непустые квартальные суммы (только те колонки, для которых есть
            // дата в header-row, остальное игнорируем — за CU могут быть годовые).
            var quartersForArticle = new List<object>();
            foreach (var (colLetter, _) in quartersByLetter)
            {
                if (!row.Cells.TryGetValue(colLetter, out var raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!TryParseFlexibleDouble(raw, out var amount)) continue;
                if (Math.Abs(amount) < 0.0005) continue;
                quartersForArticle.Add(new { Col = colLetter, AmountThousands = amount });
            }

            if (quartersForArticle.Count == 0)
            {
                _log.LogTrace(
                    "Schedule row {RowNum}: статья '{Code}' без непустых квартальных сумм — skip",
                    row.SourceRowNumber, code);
                continue;
            }

            matched++;
            var articleJson = JsonSerializer.Serialize(new
            {
                Kind         = "schedule_article",
                ChapterCode  = "1.",
                ArticleCode  = code,
                ArticleTitle = matchedRefTitle ?? title,
                FileTitle    = title,
                Quarters     = quartersForArticle,
            });
            mapped.Add(new MappedRow(
                row.SourceRowNumber, scheduleSheet, true,
                JsonDocument.Parse(articleJson), Array.Empty<RowError>()));
        }

        _log.LogInformation(
            "FinModelImportMapper: ГФ Главы 1 сборка → {Matched} matched / {Unknown} unknown (Этап 1, {Quarters} кварталов)",
            matched, skippedUnknown, quartersByLetter.Count);
        return mapped;
    }

    /// <summary>
    /// Применяет ГФ Главы 1: для каждой mapped-статьи находим WBS-узел у объекта,
    /// pre-check существующие <see cref="CostItemRaw"/> через
    /// <see cref="IListViewClient.GetCostItemsByWbsAsync"/> и для каждого квартала
    /// POST / PATCH / skip по совпадению <see cref="CostItemPeriod.Start"/>.
    /// Per-cell <see cref="RowActionLog.Actions"/> — успех или «статья отсутствует в ИСР».
    /// </summary>
    private async Task<int> ApplyChapter1ScheduleAsync(
        int siteId,
        MappedRow quartersRow,
        IReadOnlyList<MappedRow> articleRows,
        List<RowError> errors,
        List<RowActionLog> rowActions,
        CancellationToken ct)
    {
        // 1) Reconstruct ColLetter → DateTime map.
        var quartersByLetter = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var q in quartersRow.MappedValues.RootElement.GetProperty("Quarters").EnumerateArray())
        {
            var col = q.GetProperty("Col").GetString()!;
            var date = DateTime.ParseExact(
                q.GetProperty("Date").GetString()!, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None);
            quartersByLetter[col] = date;
        }

        // 2) Load WBS for the site.
        ListViewResponse<WbsRaw> wbsList;
        try
        {
            wbsList = await _listViewClient.GetWbsBySiteAsync(siteId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ГФ: failed to load WBS for siteId={SiteId}", siteId);
            errors.Add(new RowError(null, "wbs_list_failed",
                $"Не удалось получить ИСР объекта {siteId}: {ex.Message}"));
            return 0;
        }

        var wbsByCode = wbsList.Data
            .Where(w => !string.IsNullOrWhiteSpace(w.Code))
            .GroupBy(w => NormalizeWbsCode(w.Code!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        _log.LogInformation(
            "ГФ: загружено {Count} WBS-узлов объекта siteId={SiteId} (по Code: {Codes})",
            wbsList.Data.Count, siteId,
            string.Join(", ", wbsByCode.Keys.Where(c => c.StartsWith("1.", StringComparison.Ordinal)).Take(15)));

        int applied = 0;
        foreach (var articleRow in articleRows)
        {
            ct.ThrowIfCancellationRequested();
            var root = articleRow.MappedValues.RootElement;
            var code = NormalizeWbsCode(root.GetProperty("ArticleCode").GetString()!);
            var articleTitle = root.GetProperty("ArticleTitle").GetString()!;
            var sheet = articleRow.Sheet;
            var rowNum = articleRow.SourceRowNumber;
            var perRowActions = new List<string>();

            // Список ячеек для этой статьи (col, amountThousands) — нужен и для успеха,
            // и для отчёта о пропуске «нет статьи в ИСР».
            var cells = root.GetProperty("Quarters").EnumerateArray()
                .Select(q => (
                    Col: q.GetProperty("Col").GetString()!,
                    AmountThousands: q.GetProperty("AmountThousands").GetDouble()))
                .ToList();

            if (!wbsByCode.TryGetValue(code, out var wbs))
            {
                // Per-cell сообщение в формате, который запросил пользователь
                // (доп. строка для каждой непустой квартальной ячейки).
                foreach (var (col, _) in cells)
                {
                    perRowActions.Add(
                        $"для ячейки {col}{rowNum} не была добавлена информация для ГФ, " +
                        $"тк статья {code.TrimEnd('.')} отсутствует в ИСР");
                }
                rowActions.Add(new RowActionLog(rowNum, sheet, perRowActions));
                _log.LogInformation(
                    "ГФ: статья {Code} ('{Title}') отсутствует в ИСР объекта {SiteId} — {Cells} ячеек пропущено",
                    code, articleTitle, siteId, cells.Count);
                continue;
            }

            // Pre-check существующих CostItem'ов этой подстатьи.
            ListViewResponse<CostItemRaw> existing;
            try
            {
                existing = await _listViewClient.GetCostItemsByWbsAsync(wbs.ID, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ГФ: failed to load CostItems for wbsId={WbsId}", wbs.ID);
                errors.Add(new RowError(null, "costitem_list_failed",
                    $"Не удалось получить существующий ГФ для статьи {code} (wbsId={wbs.ID}): {ex.Message}"));
                continue;
            }

            // Map existing by quarter start (date-only, UTC-нечувствительно к времени).
            var existingByStart = existing.Data
                .Where(ci => ci.PlanPeriod is { } p && p.Start != default)
                .GroupBy(ci => ci.PlanPeriod!.Start.Date)
                .ToDictionary(g => g.Key, g => g.First());

            int created = 0, skipped = 0, failed = 0;
            foreach (var (col, amountThousands) in cells)
            {
                ct.ThrowIfCancellationRequested();
                if (!quartersByLetter.TryGetValue(col, out var qStart))
                {
                    // header не содержит даты для этой колонки — пропускаем тихо.
                    continue;
                }
                var qEnd = LastDayOfQuarter(qStart);
                var amountRub = Math.Round(amountThousands * 1000.0, 2, MidpointRounding.AwayFromZero);
                var quarterLabel = FormatQuarterLabel(qStart);
                var cellLabel = $"{col}{rowNum}";

                // Pre-check существующего ГФ за этот квартал: уже есть — skip без PATCH.
                // Заказчик не хочет перезатирать суммы уже импортированного ГФ повторным
                // запуском Финмодели; ручные правки в Visary остаются нетронутыми.
                // См. doc 109.
                if (existingByStart.TryGetValue(qStart.Date, out var match))
                {
                    skipped++;
                    var existingSum = match.PlanSum is double cur ? FormatRub(cur) : "—";
                    perRowActions.Add(
                        $"ГФ {cellLabel} ({quarterLabel}, статья {code.TrimEnd('.')}): уже существует " +
                        $"(сумма в Visary: {existingSum}) — пропуск");
                    continue;
                }

                try
                {
                    await _visaryClient.CreateCostItemAsync(new CostItemCreateRequest
                    {
                        WBSID = wbs.ID,
                        WBS = new VisaryRef { ID = wbs.ID },
                        PlanSum = amountRub,
                        PlanPeriod = new CostItemPeriod
                        {
                            Start = DateTime.SpecifyKind(qStart, DateTimeKind.Utc),
                            End = DateTime.SpecifyKind(qEnd, DateTimeKind.Utc),
                        },
                        Status = CostItemStatus.Plan,
                    }, ct);
                    created++;
                    perRowActions.Add(
                        $"ГФ {cellLabel} ({quarterLabel}, статья {code.TrimEnd('.')}): создано {FormatRub(amountRub)}");
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex,
                        "ГФ {Cell}: ошибка применения для wbsId={WbsId} period={Start:yyyy-MM-dd} sum={Sum}",
                        cellLabel, wbs.ID, qStart, amountRub);
                    perRowActions.Add(
                        $"ГФ {cellLabel} ({quarterLabel}, статья {code.TrimEnd('.')}): ошибка — {ex.Message}");
                    errors.Add(new RowError(col, "costitem_apply_error",
                        $"Не удалось применить ГФ {cellLabel} (статья {code}, {quarterLabel}): {ex.Message}"));
                }
            }

            _log.LogInformation(
                "ГФ: статья {Code} wbsId={WbsId} — created={Created} skipped={Skipped} failed={Failed}",
                code, wbs.ID, created, skipped, failed);
            applied += created + skipped;
            if (perRowActions.Count > 0)
                rowActions.Add(new RowActionLog(rowNum, sheet, perRowActions));
        }

        return applied;
    }

    /// <summary>Нормализация Code WBS/справочника: trim + гарантированная хвостовая точка.</summary>
    private static string NormalizeWbsCode(string code)
    {
        var s = code?.Trim() ?? string.Empty;
        if (s.Length == 0) return s;
        return s.EndsWith('.') ? s : s + ".";
    }

    /// <summary>Возвращает последний день квартала, в который попадает указанная дата.</summary>
    private static DateTime LastDayOfQuarter(DateTime quarterStart)
    {
        // quarterStart — первый день квартала (1 января / 1 апреля / 1 июля / 1 октября).
        // Конец = +3 месяца - 1 день.
        var end = quarterStart.AddMonths(3).AddDays(-1);
        return new DateTime(end.Year, end.Month, end.Day, 0, 0, 0, DateTimeKind.Unspecified);
    }

    /// <summary>«Q3 2026» для логов/сообщений журнала.</summary>
    private static string FormatQuarterLabel(DateTime quarterStart)
    {
        int q = (quarterStart.Month - 1) / 3 + 1;
        return $"Q{q} {quarterStart.Year}";
    }

    private static string FormatRub(double amount)
        => amount.ToString("N2", CultureInfo.InvariantCulture).Replace(',', ' ') + " ₽";

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
        var cleaned = raw.Replace(" ", "").Replace(" ", "");
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
