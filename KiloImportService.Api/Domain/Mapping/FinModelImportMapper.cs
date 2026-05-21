using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KiloImportService.Api.Budget;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;
using KiloImportService.Api.Domain.Mapping.Budget;
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

    public FinModelImportMapper(
        ILogger<FinModelImportMapper> log,
        ICrudClient visaryClient,
        IListViewClient listViewClient,
        IBudgetReferenceProvider budgetRef,
        IServiceScopeFactory scopeFactory)
    {
        _log = log;
        _visaryClient = visaryClient;
        _listViewClient = listViewClient;
        _budgetRef = budgetRef;
        _scopeFactory = scopeFactory;
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
                       || indicatorCols.Any(x => x.Col is not null);

        if (!anyFound)
        {
            var allAliases = FinishingTypeAliases
                .Concat(EstateClassAliases)
                .Concat(AddressAliases)
                .Concat(InnAliases)
                .Concat(BorrowerOrganizationAliases)
                .Concat(CompanyGroupAliases)
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
            var paramApply = await ApplyParametersAsync(siteId, paramRows, errors, ct);
            applied += paramApply;
            paramsApplied = paramApply > 0;
        }

        // budget upload status — управляет тем, можно ли запускать ГФ Главы 1.
        // null означает «бюджета в файле нет» — это допустимый сценарий (например,
        // повторный импорт после ручной правки бюджета в Visary), ГФ выполняем.
        bool? budgetUploadOk = null;
        if (budgetRows.Count > 0)
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
            var schedulePending = scheduleArticleRows.Count > 0 && scheduleQuartersRow is not null;
            budgetUploadOk = await UploadBudgetToVisaryAsync(
                context.SessionId, budgetRows.Count, paramsApplied, schedulePending, errors, ct);
            if (budgetUploadOk.Value)
                applied += budgetRows.Count;
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

        return new ApplyResult(applied, errors, rowActions.Count > 0 ? rowActions : null);
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

            int created = 0, patched = 0, skipped = 0, failed = 0;
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

                try
                {
                    if (existingByStart.TryGetValue(qStart.Date, out var match))
                    {
                        if (match.PlanSum is double cur && Math.Abs(cur - amountRub) < 0.01)
                        {
                            skipped++;
                            perRowActions.Add(
                                $"ГФ {cellLabel} ({quarterLabel}, статья {code.TrimEnd('.')}): сумма {FormatRub(amountRub)} совпадает — без изменений");
                        }
                        else
                        {
                            await _visaryClient.PatchCostItemAsync(match.ID, new CostItemPatchRequest
                            {
                                PlanSum = amountRub,
                            }, ct);
                            patched++;
                            perRowActions.Add(
                                $"ГФ {cellLabel} ({quarterLabel}, статья {code.TrimEnd('.')}): обновлено " +
                                $"{FormatRub(match.PlanSum ?? 0)} → {FormatRub(amountRub)}");
                        }
                    }
                    else
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
                "ГФ: статья {Code} wbsId={WbsId} — created={Created} patched={Patched} skipped={Skipped} failed={Failed}",
                code, wbs.ID, created, patched, skipped, failed);
            applied += created + patched + skipped;
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
