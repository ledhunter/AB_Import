using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Visary.Api.Common;
using Visary.Api.Dto;

namespace Visary.Api.ListView;

public interface IListViewClient
{
    Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(
        string? search = null, int pageSize = 200, CancellationToken ct = default);

    Task<ListViewResponse<ConstructionProjectRaw>> GetProjectByIdAsync(
        int projectId, CancellationToken ct = default);

    Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
        int projectId, CancellationToken ct = default);

    /// <summary>
    /// Поиск ОКС внутри проекта по ключам (НПС, Этап) — нужен импорту Помещений,
    /// который теперь резолвит Site per-row (см. doc_project/101-rooms-multi-site-by-project.md).
    /// POST <c>listview/constructionsite/onetomany/Project?associationId={projectId}</c>
    /// с <c>Filter [["ConstructionProjectNumber","=",X],"and",["StageNumber","=",Y]]</c>.
    /// Параметры опциональны: если оба null — эквивалентно <see cref="GetSitesByProjectAsync"/>.
    /// </summary>
    Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAndKeysAsync(
        int projectId, string? projectNumber, string? stageNumber,
        CancellationToken ct = default);

    Task<ConstructionSiteRaw?> GetSiteByIdAsync(
        int siteId, CancellationToken ct = default);

    Task<ConstructionSiteRaw?> GetSiteByProjectAndIdAsync(
        int projectId, int siteId, CancellationToken ct = default);

    /// <summary>
    /// Поиск Site по любой комбинации (РНС, НПС, Этап) — три стратегии из
    /// сценария импорта rooms-form (см. RoomImport/room_sa_create.puml):
    ///   1) РНС + Этап         (передать permission+stage, projectNum=null)
    ///   2) РНС + НПС + Этап   (передать всё)
    ///   3) НПС + Этап         (передать project+stage, permission=null)
    /// Не указанные параметры в фильтр не включаются. Если все null — выбрасывает ArgumentException.
    /// </summary>
    Task<ListViewResponse<ConstructionSiteRaw>> FindSitesAsync(
        string? permissionNumber, string? projectNumber, string? stageNumber,
        CancellationToken ct = default);

    Task<ListViewResponse<ConstructionSiteIndicatorRaw>> GetIndicatorsBySiteAsync(
        int siteId, string? titleFilter = null, CancellationToken ct = default);

    Task<ListViewResponse<ConstructionSiteIndicatorValueRaw>> GetIndicatorValuesByIndicatorAsync(
        int indicatorId, CancellationToken ct = default);

    /// <summary>
    /// Сделки (<c>deal</c>) внутри проекта строительства. POST
    /// <c>listview/deal/onetomany/ConstructionProject?associationId={projectId}</c>.
    /// При указании обоих фильтров формируется составной filter
    /// <c>[["LmID","=",X],"and",["DocNumber","=",Y]]</c> — используется FinModel-импортом
    /// для pre-check существования сделки до записей в Объекте (см. doc 104).
    /// </summary>
    Task<ListViewResponse<DealRaw>> GetDealsByProjectAsync(
        int projectId, string? lmIdFilter = null, string? docNumberFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Глобальный <c>listview/deal</c>. Параметры опциональны: при обоих заданных собирается
    /// <c>[["LmID","=",X],"and",["DocNumber","=",Y]]</c> через <see cref="ListViewClient.FilterAnd"/>;
    /// при одном — простой <c>=</c>-фильтр; без обоих — пустой Filter (первая страница).
    /// FinModel использует этот метод как fallback после <see cref="GetDealsByProjectAsync"/>:
    /// если сделки нет в текущем проекте, но она нашлась глобально — значит она привязана
    /// к чужому проекту (см. doc 104 v1.2).
    /// </summary>
    Task<ListViewResponse<DealRaw>> GetDealsAsync(
        string? lmIdFilter = null, string? docNumberFilter = null,
        CancellationToken ct = default);

    Task<ListViewResponse<OrganizationRaw>> GetOrganizationsByClientIdAsync(
        string clientId, CancellationToken ct = default);

    /// <summary>
    /// Поиск группы компаний (<c>companygroup</c>) по точному наименованию.
    /// POST <c>/api/visary/listview/companygroup</c> с
    /// <c>Filter ["Title","=","{title}"]</c>. Используется FinModel-импортом для
    /// привязки организации-застройщика к материнской группе (поле <c>Group</c>);
    /// см. doc_project/100-finmodel-companygroup-link.md.
    /// </summary>
    Task<ListViewResponse<CompanyGroupRaw>> GetCompanyGroupsByTitleAsync(
        string title, CancellationToken ct = default);

    /// <summary>
    /// Список <c>projectmanagement</c>-записей, привязанных к объекту строительства
    /// через manytomany (POST <c>/api/visary/listview/constructionsite/manytomany/projectmanagement?associationId={siteId}</c>).
    /// Возвращает все роли (Застройщик, Технический заказчик, …); фильтрация по
    /// <see cref="ProjectManagementRaw.Role"/> и <see cref="ProjectManagementRaw.Organization"/>
    /// делается на стороне вызывающего.
    /// </summary>
    Task<ListViewResponse<ProjectManagementRaw>> GetProjectManagementsBySiteAsync(
        int siteId, CancellationToken ct = default);

    /// <summary>
    /// Список <c>projectmanagement</c>-записей в рамках проекта (onetomany).
    /// POST <c>/api/visary/listview/projectmanagement/onetomany/Project?associationId={projectId}</c>.
    /// <para>
    /// Используется в импорте Помещений, чтобы переиспользовать существующий PM из
    /// другого объекта того же проекта (не плодить дубликаты <see cref="ProjectManagementRaw"/>
    /// между сайтами). Фильтры по <paramref name="organizationId"/>/<paramref name="roleId"/>
    /// уходят на сервер как <c>["Organization","contains","ID:{id}"]</c>.
    /// </para>
    /// </summary>
    Task<ListViewResponse<ProjectManagementRaw>> GetProjectManagementsByProjectAsync(
        int projectId, int? organizationId = null, int? roleId = null,
        CancellationToken ct = default);

    Task<ListViewResponse<RoomRaw>> GetRoomsBySiteAsync(
        int siteId, string? uniqueNumberFilter = null, CancellationToken ct = default);

    Task<ListViewResponse<RoomRaw>> GetRoomsBySectionAsync(
        int sectionId, string? uniqueNumberFilter = null, CancellationToken ct = default);

    Task<ListViewResponse<PercentBetRaw>> GetPercentBetsAsync(
        string? lmIdFilter = null, int? dealId = null, CancellationToken ct = default);

    Task<ListViewResponse<ConstructionSectionRaw>> GetSectionsBySiteAsync(
        int siteId, string? titleFilter = null, CancellationToken ct = default);

    Task<ListViewResponse<ShareAgreementRaw>> GetShareAgreementsByRoomAsync(
        int roomId, string? numberFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Глобальный поиск ДДУ (<c>shareagreement</c>) по комбинации признаков из
    /// строки файла импорта Помещений — № договора, тип помещения, № квартиры
    /// (экспликация), этап, НПС. Используется чтобы избежать дубликатов: даже
    /// если ДДУ есть в системе, но НЕ привязан к комнате, его нужно переиспользовать
    /// через PATCH вместо CREATE. Не указанные параметры в фильтр не включаются.
    /// </summary>
    Task<ListViewResponse<ShareAgreementRaw>> FindShareAgreementsAsync(
        string? number,
        int? roomKindId,
        string? conditionalNumber,
        string? stageNumber,
        string? projectNumber,
        CancellationToken ct = default);

    Task<ListViewResponse<CadastralAreaFull>> ListCadastralAreasAsync(
        string? cadastralNumFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Список WBS-записей (главы и подстатьи бюджета) у проекта.
    /// Используется для поиска существующей главы по Title/Code перед созданием подстатьи.
    /// </summary>
    Task<ListViewResponse<WbsRaw>> GetWbsByProjectAsync(
        int projectId, CancellationToken ct = default);

    /// <summary>
    /// Список WBS-записей объекта строительства (дерево ИСР ОКСа). Отдельно от
    /// <see cref="GetWbsByProjectAsync"/>: в большом проекте обычно несколько ОКСов
    /// со своими копиями подстатей — для импорта ГФ нужны статьи именно выбранного
    /// объекта (а не «нашего» дубликата из соседнего ОКСа).
    /// HAR: <c>POST /api/visary/listview/wbs/onetomany/ConstructionSite?associationId={siteId}</c>.
    /// </summary>
    Task<ListViewResponse<WbsRaw>> GetWbsBySiteAsync(
        int siteId, CancellationToken ct = default);

    /// <summary>
    /// Список существующих строк ГФ (<c>costitem</c>) у конкретной подстатьи ИСР —
    /// нужен перед POST'ом, чтобы не плодить дубликаты (на сервере уникальности по
    /// (<see cref="CostItemRaw.WBSID"/>, <see cref="CostItemRaw.PlanPeriod"/>) нет).
    /// HAR: <c>POST /api/visary/listview/costitem/onetomany/WBS?associationId={wbsId}</c>.
    /// </summary>
    Task<ListViewResponse<CostItemRaw>> GetCostItemsByWbsAsync(
        int wbsId, CancellationToken ct = default);

    // ─── Справочники (list для резолвинга «название → ID») ──────────────────
    // Используются мапперами импорта: тянем справочник один раз на сессию,
    // строим Title → ID словарь по живым данным (не хардкод switch'ем).
    // Например, FinModelImportMapper использует ListFinishingMaterialsAsync
    // для маппинга «Тип отделки» из Excel в FinishingMaterialId.
    Task<ListViewResponse<TownRaw>>                ListTownsAsync(string? titleFilter = null, CancellationToken ct = default);
    Task<ListViewResponse<RegionRaw>>              ListRegionsAsync(string? titleFilter = null, CancellationToken ct = default);
    Task<ListViewResponse<ProjectTypeRaw>>         ListProjectTypesAsync(CancellationToken ct = default);
    Task<ListViewResponse<InflationCalcMethodRaw>> ListInflationCalcMethodsAsync(CancellationToken ct = default);
    Task<ListViewResponse<EstateClassRaw>>         ListEstateClassesAsync(CancellationToken ct = default);
    Task<ListViewResponse<BuildingMaterialRaw>>    ListBuildingMaterialsAsync(CancellationToken ct = default);
    Task<ListViewResponse<FinishingMaterialRaw>>   ListFinishingMaterialsAsync(CancellationToken ct = default);
    Task<ListViewResponse<RoomKindRaw>>            ListRoomKindsAsync(CancellationToken ct = default);
}

public sealed class ListViewClient : VisaryHttpBase<ListViewClient>, IListViewClient
{
    private static readonly string[] ProjectColumns =
        ["ID", "Title", "IdentifierKK", "IdentifierZPLM", "Hidden"];

    private static readonly string[] ProjectFullColumns =
        ["ID", "Title", "Author", "ProjectManager", "Executor", "Sponsor",
         "Stage", "Type", "Region", "Town", "Date",
         "Developer", "DeveloperPIN", "DeveloperGroup",
         "IdentifierKK", "IdentifierZPLM", "ConstructionProjectNumber",
         "Description", "FinancingStart", "Version", "Hidden"];

    private static readonly string[] SiteColumns =
        ["ID", "Title", "ConstructionProjectId", "ConstructionPermissionNumber",
         "ConstructionProjectNumber", "StageNumber", "RegionId", "TownId", "Address",
         "Hidden", "Version", "FinishingMaterialId"];

    private static readonly string[] IndicatorColumns =
        ["ID", "Title", "ConstructionSite", "GoalValue", "GoalDate", "Indicator",
         "Group", "Project", "Comment", "SortOrder", "MainValue", "MainTextValue",
         "LastUpdate", "LastPlanValue", "LastForecastValue", "LastValue", "MainSource", "Version"];

    private static readonly string[] IndicatorValueColumns =
        ["ID", "Title", "ConstructionSiteIndicator", "Date", "Value", "PlanValue",
         "ForecastValue", "Stage", "IsUnlimited", "IndicatorGroup", "TextValue",
         "ProjectDoc", "Section", "Site", "SortOrder", "Version"];

    private static readonly string[] DealColumns =
        ["ID", "Title", "LmID", "DocNumber", "ConstructionProject", "Organization",
         "GroupName", "CreditSum", "DealStartDate", "DealEndDate"];

    private static readonly string[] OrganizationColumns =
        ["ID", "Title", "Status", "INN", "SRO", "ClientID", "Region", "Address",
         "CEO", "Email", "Phone", "AddInfo", "Group", "Town", "Code", "CurrentUser",
         "OGRN", "KPP", "Category", "Hidden"];

    private static readonly string[] ProjectManagementColumns =
        ["ID", "Project", "Role", "Organization", "DateStart", "DateEnd", "Affiliation", "Title", "Version"];

    // Колонки для companygroup: минимум для резолва Title → ID. Поле Hidden — Visary
    // отдаёт его для всех справочников; запрашиваем явно, чтобы по умолчанию иметь
    // возможность отбросить скрытые записи на стороне вызывающего.
    private static readonly string[] CompanyGroupColumns =
        ["ID", "Title", "Code", "Hidden"];

    private static readonly string[] RoomColumns =
        ["ID", "Title", "Site", "Section", "Number", "Floor", "Kind", "RoomsNumber",
         "IsStudio", "TotalArea", "LivingArea", "Description", "Cost",
         "IsSeparateEntrance", "IsShowcaseWindows", "TotalAreaWithoutSummerRoom",
         "SummerRoomArea", "CostForOne", "ExplicationNumber", "BuildingSection",
         "UniqueNumber", "ProjectArea", "RoomPurpose", "ParkingPlaceType",
         "CadastralNumber", "IsWithdrawn", "RoomCategory",
         "ActiveShareAgreement", "CandidateShareAgreement",
         "ActiveEscrowAccount", "CandidateEscrowAccount",
         "CalculatedCostPerM", "MarketCostPerM", "ZalogCostPerM"];

    private static readonly string[] PercentBetColumns =
        ["ID", "LmID", "BaseRateType", "PercentKind", "Deal", "Rate", "CommissionSum",
         "Currency", "StandardRate", "SpecialRate", "StartDate", "EndDate",
         "PaymentCurrency", "SpecialRateCalc", "BasePart", "FloatRateMin", "FloatRateMax",
         "Advance", "DateCreate", "ModifiedAt"];

    private static readonly string[] SectionColumns =
        ["ID", "Title", "ConstructionSite", "Type", "Stage", "HasUndergroundStage",
         "Description", "HasLift", "ResQuantity", "NonresQuantity", "OtherNonresQuantity",
         "ParkingQuantity", "ResProjectArea", "ResAreaWithoutSummerRoom", "NonresArea",
         "OtherNonresArea", "ParkingArea", "AvgResArea", "AvgResAreaWithoutSummerRoom",
         "ResPercentage", "SectionID", "ClaimedCost", "BuildingMaterial",
         "CostPerUnit", "TotalCost", "Version"];

    // Минимальный набор колонок для справочников: достаточно для резолвинга «название → ID».
    // Если нужен полный DTO — берите через ICrudClient.GetXxxByIdAsync.
    private static readonly string[] DictionaryColumns = ["ID", "Title", "Hidden"];

    // RoomKind дополнительно отдаёт RoomCategory — нужна импортеру помещений,
    // чтобы решить, в какое поле положить площадь (Жилое: 1 — ProjectArea;
    // Нежилое: ≠1 — TotalArea, ProjectArea=0). См. RoomsFormImportMapper.
    private static readonly string[] RoomKindColumns = ["ID", "Title", "Hidden", "RoomCategory"];

    private static readonly string[] WbsColumns =
        ["ID", "Title", "Code", "ParentID", "Parent", "ProjectID", "Project",
         "ConstructionSite", "DeclaredSum", "ConfirmedSum"];

    // Колонки для costitem listview (имена 1:1 с HAR Context/har ГФ.txt).
    // PlanQuarter/PlanYear — derived-поля; в ответе приходят, для дедупликации
    // используем PlanPeriod (он же есть).
    private static readonly string[] CostItemColumns =
        ["ID", "WBS", "Snapshot", "PlanSum", "Status", "PlanPeriod", "ProjectDoc",
         "Version", "PlanMonth", "PlanQuarter", "PlanYear"];

    private static readonly string[] ShareAgreementColumns =
        ["ID", "Title", "Number", "Date", "ConstructionPermitNumber", "ConstructionPermitDate",
         "ProjectNumber", "ProjectTitle", "DeveloperPIN", "DeveloperINN",
         "StateRegistrationStatus", "StateRegistrationNumber", "FilingDate", "RegistrationDate",
         "SerialNumber", "RoomKind", "HouseNumber", "SectionNumber", "RoomNumber",
         "ConditionalNumber", "TotalArea", "TotalLivingArea", "CadastralNumber", "Cost",
         "Deadline", "DepositedAmount", "IsBorrowedFunds", "IsPreferentialRate",
         "BudgetFundsAmount", "Street", "DepositorFullName", "MotherFundAmount",
         "IsRegisteredProvided", "HouseNumberPermit", "Site", "Project",
         "StageNumber", "Room", "ValidityStatus", "RoomKindRef"];

    // Visary API ожидает строку "null" в Sorts (не JSON-null) — проверено на стенде.
    // Если поставить null или опустить — сервер возвращает 400.
    private const string SortsNullSentinel = "null";

    public ListViewClient(
        HttpClient http,
        IOptionsMonitor<VisaryOptions> options,
        ILogger<ListViewClient> log)
        : base(http, options, log) { }

    // ─── Projects ────────────────────────────────────────────────────────────

    public Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(
        string? search, int pageSize, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Project,
            PageSkip = 0,
            PageSize = pageSize,
            Columns = ProjectColumns,
            SearchString = search ?? string.Empty,
        };

        _log.LogDebug("Visary → GET listview/{Mnemonic} search='{Search}'", VisaryMnemonics.Project, search);
        return PostListViewAsync<ConstructionProjectRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Project}", body, VisaryMnemonics.Project, ct);
    }

    public Task<ListViewResponse<ConstructionProjectRaw>> GetProjectByIdAsync(int projectId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Project,
            PageSkip = 0,
            PageSize = 1,
            Columns = ProjectFullColumns,
            Filter = FilterByInt("ID", projectId),
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET listview/{Mnemonic} by id={Id}", VisaryMnemonics.Project, projectId);
        return PostListViewAsync<ConstructionProjectRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Project}",
            body, $"{VisaryMnemonics.Project} id={projectId}", ct);
    }

    // ─── Sites ───────────────────────────────────────────────────────────────

    public Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(int projectId, CancellationToken ct)
        => GetSitesByProjectAndKeysAsync(projectId, projectNumber: null, stageNumber: null, ct);

    public Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAndKeysAsync(
        int projectId, string? projectNumber, string? stageNumber, CancellationToken ct)
    {
        // Собираем AND-фильтр только из непустых ключей (паттерн FindSitesAsync).
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(projectNumber))
            parts.Add(FilterByString("ConstructionProjectNumber", projectNumber));
        if (!string.IsNullOrWhiteSpace(stageNumber))
            parts.Add(FilterByString("StageNumber", stageNumber));
        string? filter = parts.Count == 0 ? null
            : parts.Aggregate((a, b) => FilterAnd(a, b));

        // ВАЖНО: Visary onetomany-эндпоинт требует Filter-ключ в body, даже если он null
        // (как в эталонном запросе из задачи). Передаём его всегда.
        var body = new
        {
            Mnemonic = VisaryMnemonics.Site,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = SiteColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug(
            "Visary → POST listview/{Mnemonic}/onetomany/Project projectId={ProjectId} proj='{P}' stage='{S}'",
            VisaryMnemonics.Site, projectId, projectNumber, stageNumber);
        return PostListViewAsync<ConstructionSiteRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/onetomany/Project?associationId={projectId}",
            body,
            $"{VisaryMnemonics.Site}/onetomany/Project id={projectId} proj={projectNumber} stage={stageNumber}",
            ct);
    }

    public Task<ConstructionSiteRaw?> GetSiteByIdAsync(int siteId, CancellationToken ct)
        => throw new NotSupportedException(
            "GetSiteByIdAsync не поддерживается: используйте GetSiteByProjectAndIdAsync(projectId, siteId).");

    public async Task<ConstructionSiteRaw?> GetSiteByProjectAndIdAsync(
        int projectId, int siteId, CancellationToken ct)
    {
        var response = await GetSitesByProjectAsync(projectId, ct);
        return response.Data.FirstOrDefault(s => s.ID == siteId);
    }

    public Task<ListViewResponse<ConstructionSiteRaw>> FindSitesAsync(
        string? permissionNumber, string? projectNumber, string? stageNumber, CancellationToken ct)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(permissionNumber))
            parts.Add(FilterByString("ConstructionPermissionNumber", permissionNumber));
        if (!string.IsNullOrWhiteSpace(projectNumber))
            parts.Add(FilterByString("ConstructionProjectNumber", projectNumber));
        if (!string.IsNullOrWhiteSpace(stageNumber))
            parts.Add(FilterByString("StageNumber", stageNumber));

        if (parts.Count == 0)
            throw new ArgumentException(
                "FindSitesAsync требует хотя бы один из параметров: permissionNumber/projectNumber/stageNumber.");

        // Связываем фильтры через AND слева направо: ((f1 AND f2) AND f3).
        var filter = parts.Aggregate((a, b) => FilterAnd(a, b));

        var body = new
        {
            Mnemonic = VisaryMnemonics.Site,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = SiteColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET listview/{Mnemonic} find perm='{P}' proj='{Pr}' stage='{S}'",
            VisaryMnemonics.Site, permissionNumber, projectNumber, stageNumber);
        return PostListViewAsync<ConstructionSiteRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}",
            body,
            $"{VisaryMnemonics.Site} find(perm={permissionNumber},proj={projectNumber},stage={stageNumber})",
            ct);
    }

    // ─── Indicators (ТЭПы) ───────────────────────────────────────────────────

    public Task<ListViewResponse<ConstructionSiteIndicatorRaw>> GetIndicatorsBySiteAsync(
        int siteId, string? titleFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.SiteIndicator,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = IndicatorColumns,
            // contains, не "=", потому что Title показателя в Visary может содержать
            // хвостовые пробелы ("Площадь застройки ") — UI Visary тоже использует contains.
            // Точное соответствие делаем уже в коде через Trim()+OrdinalIgnoreCase.
            Filter = titleFilter != null ? FilterByStringContains("Title", titleFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic}/onetomany/ConstructionSite siteId={SiteId}",
            VisaryMnemonics.SiteIndicator, siteId);
        return PostListViewAsync<ConstructionSiteIndicatorRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.SiteIndicator}/onetomany/ConstructionSite?associationId={siteId}",
            body, $"{VisaryMnemonics.SiteIndicator} siteId={siteId}", ct);
    }

    public Task<ListViewResponse<ConstructionSiteIndicatorValueRaw>> GetIndicatorValuesByIndicatorAsync(
        int indicatorId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.SiteIndicatorValue,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = IndicatorValueColumns,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic}/onetomany/ConstructionSiteIndicator indicatorId={Id}",
            VisaryMnemonics.SiteIndicatorValue, indicatorId);
        return PostListViewAsync<ConstructionSiteIndicatorValueRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.SiteIndicatorValue}/onetomany/ConstructionSiteIndicator?associationId={indicatorId}",
            body, $"{VisaryMnemonics.SiteIndicatorValue} indicatorId={indicatorId}", ct);
    }

    // ─── Deals ───────────────────────────────────────────────────────────────

    public Task<ListViewResponse<DealRaw>> GetDealsByProjectAsync(
        int projectId, string? lmIdFilter, string? docNumberFilter, CancellationToken ct)
    {
        // Filter собирается из 0/1/2 частей: при двух — соединяем через FilterAnd,
        // получая JSON-форму [["LmID","=",X],"and",["DocNumber","=",Y]].
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(lmIdFilter))
            parts.Add(FilterByString("LmID", lmIdFilter));
        if (!string.IsNullOrWhiteSpace(docNumberFilter))
            parts.Add(FilterByString("DocNumber", docNumberFilter));
        string? filter = parts.Count == 0 ? null
            : parts.Aggregate((a, b) => FilterAnd(a, b));

        var body = new
        {
            Mnemonic = VisaryMnemonics.Deal,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = DealColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug(
            "Visary → GET {Mnemonic}/onetomany/ConstructionProject projectId={ProjectId} lmId='{LmId}' docNumber='{DocNumber}'",
            VisaryMnemonics.Deal, projectId, lmIdFilter, docNumberFilter);
        return PostListViewAsync<DealRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Deal}/onetomany/ConstructionProject?associationId={projectId}",
            body, $"{VisaryMnemonics.Deal}/onetomany/ConstructionProject id={projectId}", ct);
    }

    public Task<ListViewResponse<DealRaw>> GetDealsAsync(
        string? lmIdFilter, string? docNumberFilter, CancellationToken ct)
    {
        // Тот же приём, что в GetDealsByProjectAsync: 0/1/2 части → опционально склеиваем
        // через FilterAnd. Без аргументов получается «весь список» (первая страница) —
        // используется проксей-контроллером VisaryEntitiesController для UI-дропдауна.
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(lmIdFilter))
            parts.Add(FilterByString("LmID", lmIdFilter));
        if (!string.IsNullOrWhiteSpace(docNumberFilter))
            parts.Add(FilterByString("DocNumber", docNumberFilter));
        string? filter = parts.Count == 0 ? null
            : parts.Aggregate((a, b) => FilterAnd(a, b));

        var body = new
        {
            Mnemonic = VisaryMnemonics.Deal,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = DealColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic} lmId='{LmId}' docNumber='{DocNumber}'",
            VisaryMnemonics.Deal, lmIdFilter, docNumberFilter);
        return PostListViewAsync<DealRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Deal}", body, VisaryMnemonics.Deal, ct);
    }

    // ─── Organizations ───────────────────────────────────────────────────────

    public Task<ListViewResponse<OrganizationRaw>> GetOrganizationsByClientIdAsync(
        string clientId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Organization,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = OrganizationColumns,
            Filter = FilterByString("ClientID", clientId),
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic} clientId='{ClientId}'", VisaryMnemonics.Organization, clientId);
        return PostListViewAsync<OrganizationRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Organization}",
            body, $"{VisaryMnemonics.Organization} clientId={clientId}", ct);
    }

    // ─── CompanyGroup ────────────────────────────────────────────────────────

    public Task<ListViewResponse<CompanyGroupRaw>> GetCompanyGroupsByTitleAsync(
        string title, CancellationToken ct)
    {
        // Точное "=" по Title. На практике Visary матчит без учёта хвостовых пробелов
        // (так же, как для ДДУ — см. doc 76). Если получим >1 запись с одинаковым
        // Title, не угадываем — вызывающий должен среагировать как «не нашли».
        var body = new
        {
            Mnemonic = VisaryMnemonics.CompanyGroup,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = CompanyGroupColumns,
            Filter = FilterByString("Title", title),
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic} title='{Title}'", VisaryMnemonics.CompanyGroup, title);
        return PostListViewAsync<CompanyGroupRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.CompanyGroup}",
            body, $"{VisaryMnemonics.CompanyGroup} title={title}", ct);
    }

    // ─── ProjectManagement ──────────────────────────────────────────────────
    // Manytomany через `constructionsite` — Visary возвращает список ролей-привязок
    // (Застройщик, Тех.заказчик, …), относящихся к данному объекту строительства.

    public Task<ListViewResponse<ProjectManagementRaw>> GetProjectManagementsBySiteAsync(
        int siteId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.ProjectManagement,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = ProjectManagementColumns,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug(
            "Visary → GET {Site}/manytomany/{PM} siteId={SiteId}",
            VisaryMnemonics.Site, VisaryMnemonics.ProjectManagement, siteId);
        return PostListViewAsync<ProjectManagementRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/manytomany/{VisaryMnemonics.ProjectManagement}?associationId={siteId}",
            body, $"{VisaryMnemonics.Site}/manytomany/{VisaryMnemonics.ProjectManagement} siteId={siteId}", ct);
    }

    public Task<ListViewResponse<ProjectManagementRaw>> GetProjectManagementsByProjectAsync(
        int projectId, int? organizationId, int? roleId, CancellationToken ct)
    {
        // Visary не различает "=" и "contains" для VisaryRef-полей правильно — Postman'ом
        // подтверждено, что работает только `contains "ID:{id}"`. См. doc 75.
        string? filter = null;
        if (organizationId is int orgId && roleId is int rId)
            filter = FilterAnd(
                FilterByRefIdContains("Organization", orgId),
                FilterByRefIdContains("Role", rId));
        else if (organizationId is int o)
            filter = FilterByRefIdContains("Organization", o);
        else if (roleId is int r)
            filter = FilterByRefIdContains("Role", r);

        var body = new
        {
            Mnemonic = VisaryMnemonics.ProjectManagement,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = ProjectManagementColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug(
            "Visary → GET {PM}/onetomany/Project projectId={ProjectId} orgId={OrgId} roleId={RoleId}",
            VisaryMnemonics.ProjectManagement, projectId, organizationId, roleId);
        return PostListViewAsync<ProjectManagementRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.ProjectManagement}/onetomany/Project?associationId={projectId}",
            body, $"{VisaryMnemonics.ProjectManagement}/onetomany/Project projectId={projectId}", ct);
    }

    // ─── Rooms ───────────────────────────────────────────────────────────────

    public Task<ListViewResponse<RoomRaw>> GetRoomsBySiteAsync(
        int siteId, string? uniqueNumberFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Room,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = RoomColumns,
            Filter = uniqueNumberFilter != null ? FilterByString("UniqueNumber", uniqueNumberFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic}/onetomany/Site siteId={SiteId}", VisaryMnemonics.Room, siteId);
        return PostListViewAsync<RoomRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Room}/onetomany/Site?associationId={siteId}",
            body, $"{VisaryMnemonics.Room}/onetomany/Site siteId={siteId}", ct);
    }

    public Task<ListViewResponse<RoomRaw>> GetRoomsBySectionAsync(
        int sectionId, string? uniqueNumberFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Room,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = RoomColumns,
            Filter = uniqueNumberFilter != null ? FilterByString("UniqueNumber", uniqueNumberFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic}/onetomany/Section sectionId={SectionId}",
            VisaryMnemonics.Room, sectionId);
        return PostListViewAsync<RoomRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Room}/onetomany/Section?associationId={sectionId}",
            body, $"{VisaryMnemonics.Room}/onetomany/Section sectionId={sectionId}", ct);
    }

    // ─── PercentBet ──────────────────────────────────────────────────────────

    public Task<ListViewResponse<PercentBetRaw>> GetPercentBetsAsync(
        string? lmIdFilter, int? dealId, CancellationToken ct)
    {
        string? filter = null;
        if (lmIdFilter != null && dealId != null)
            filter = FilterAnd(FilterByString("LmID", lmIdFilter), FilterByRefId("Deal", dealId.Value));
        else if (lmIdFilter != null)
            filter = FilterByString("LmID", lmIdFilter);
        else if (dealId != null)
            filter = FilterByRefId("Deal", dealId.Value);

        var body = new
        {
            Mnemonic = VisaryMnemonics.PercentBet,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = PercentBetColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic} lmId='{LmId}' dealId={DealId}",
            VisaryMnemonics.PercentBet, lmIdFilter, dealId);
        return PostListViewAsync<PercentBetRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.PercentBet}",
            body, VisaryMnemonics.PercentBet, ct);
    }

    // ─── Sections ────────────────────────────────────────────────────────────

    public Task<ListViewResponse<ConstructionSectionRaw>> GetSectionsBySiteAsync(
        int siteId, string? titleFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Section,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = SectionColumns,
            Filter = titleFilter != null ? FilterByString("Title", titleFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic}/onetomany/ConstructionSite siteId={SiteId}",
            VisaryMnemonics.Section, siteId);
        return PostListViewAsync<ConstructionSectionRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Section}/onetomany/ConstructionSite?associationId={siteId}",
            body, $"{VisaryMnemonics.Section} siteId={siteId}", ct);
    }

    // ─── ShareAgreements ─────────────────────────────────────────────────────

    public Task<ListViewResponse<ShareAgreementRaw>> GetShareAgreementsByRoomAsync(
        int roomId, string? numberFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.ShareAgreement,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = ShareAgreementColumns,
            Filter = numberFilter != null ? FilterByString("Number", numberFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic}/onetomany/Room roomId={RoomId}",
            VisaryMnemonics.ShareAgreement, roomId);
        return PostListViewAsync<ShareAgreementRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.ShareAgreement}/onetomany/Room?associationId={roomId}",
            body, $"{VisaryMnemonics.ShareAgreement} roomId={roomId}", ct);
    }

    public Task<ListViewResponse<ShareAgreementRaw>> FindShareAgreementsAsync(
        string? number, int? roomKindId, string? conditionalNumber,
        string? stageNumber, string? projectNumber, CancellationToken ct)
    {
        // Собираем AND-фильтр только из ненулевых параметров (см. паттерн FindSitesAsync).
        var parts = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(number))
            parts.Add(FilterByString("Number", number));
        if (roomKindId is int kid)
            parts.Add(FilterByRefIdContains("RoomKindRef", kid));
        if (!string.IsNullOrWhiteSpace(conditionalNumber))
            parts.Add(FilterByString("ConditionalNumber", conditionalNumber));
        if (!string.IsNullOrWhiteSpace(stageNumber))
            parts.Add(FilterByString("StageNumber", stageNumber));
        if (!string.IsNullOrWhiteSpace(projectNumber))
            parts.Add(FilterByString("ProjectNumber", projectNumber));

        if (parts.Count == 0)
            throw new ArgumentException(
                "FindShareAgreementsAsync: нужно указать хотя бы один параметр для фильтра.");

        var filter = parts.Aggregate((a, b) => FilterAnd(a, b));

        var body = new
        {
            Mnemonic = VisaryMnemonics.ShareAgreement,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = ShareAgreementColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug(
            "Visary → GET {Mnemonic} number='{Number}' kindId={Kind} cond='{Cond}' stage='{Stage}' projectNum='{Proj}'",
            VisaryMnemonics.ShareAgreement, number, roomKindId, conditionalNumber, stageNumber, projectNumber);
        return PostListViewAsync<ShareAgreementRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.ShareAgreement}",
            body, $"{VisaryMnemonics.ShareAgreement} find", ct);
    }

    // ─── CadastralAreas list ─────────────────────────────────────────────────

    public Task<ListViewResponse<CadastralAreaFull>> ListCadastralAreasAsync(
        string? cadastralNumFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.CadastralArea,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = new[] { "ID", "CadastralNum", "Area", "EGRNNumber" },
            Filter = cadastralNumFilter != null ? FilterByString("CadastralNum", cadastralNumFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };
        _log.LogDebug("Visary → GET listview/{Mnemonic}", VisaryMnemonics.CadastralArea);
        return PostListViewAsync<CadastralAreaFull>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.CadastralArea}",
            body, VisaryMnemonics.CadastralArea, ct);
    }

    // ─── WBS (ИСР — главы и подстатьи бюджета) ───────────────────────────────

    public Task<ListViewResponse<WbsRaw>> GetWbsByProjectAsync(int projectId, CancellationToken ct)
    {
        // listview/wbs/onetomany/ConstructionProject — паттерн «дочерние сущности проекта»,
        // как для Site/Indicator. Возвращает все WBS-записи проекта (главы и подстатьи)
        // одной страницей. Code/ParentID позволяют построить иерархию на клиенте.
        var body = new
        {
            Mnemonic = VisaryMnemonics.Wbs,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = WbsColumns,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };
        _log.LogDebug("Visary → GET listview/{Mnemonic}/onetomany/ConstructionProject projectId={ProjectId}",
            VisaryMnemonics.Wbs, projectId);
        return PostListViewAsync<WbsRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Wbs}/onetomany/ConstructionProject?associationId={projectId}",
            body, $"{VisaryMnemonics.Wbs}/onetomany/ConstructionProject id={projectId}", ct);
    }

    public Task<ListViewResponse<WbsRaw>> GetWbsBySiteAsync(int siteId, CancellationToken ct)
    {
        // Используется импортом ГФ Финмодели — нужны WBS-подстатьи именно выбранного ОКСа
        // (в большом проекте подстатьи Главы 1 могут существовать в каждом сайте отдельно).
        // HAR: listview/wbs/onetomany/ConstructionSite?associationId={siteId}.
        var body = new
        {
            Mnemonic = VisaryMnemonics.Wbs,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = WbsColumns,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };
        _log.LogDebug("Visary → GET listview/{Mnemonic}/onetomany/ConstructionSite siteId={SiteId}",
            VisaryMnemonics.Wbs, siteId);
        return PostListViewAsync<WbsRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Wbs}/onetomany/ConstructionSite?associationId={siteId}",
            body, $"{VisaryMnemonics.Wbs}/onetomany/ConstructionSite id={siteId}", ct);
    }

    // ─── CostItem (ГФ — график финансирования) ──────────────────────────────

    public Task<ListViewResponse<CostItemRaw>> GetCostItemsByWbsAsync(int wbsId, CancellationToken ct)
    {
        // POST listview/costitem/onetomany/WBS?associationId={wbsId} — все строки ГФ
        // для конкретной подстатьи. Используется импортом ГФ для дедупликации:
        // сервер не проверяет уникальность (WBSID, PlanPeriod) сам.
        var body = new
        {
            Mnemonic = VisaryMnemonics.CostItem,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = CostItemColumns,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };
        _log.LogDebug("Visary → GET listview/{Mnemonic}/onetomany/WBS wbsId={WbsId}",
            VisaryMnemonics.CostItem, wbsId);
        return PostListViewAsync<CostItemRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.CostItem}/onetomany/WBS?associationId={wbsId}",
            body, $"{VisaryMnemonics.CostItem}/onetomany/WBS id={wbsId}", ct);
    }

    // ─── Справочники ─────────────────────────────────────────────────────────

    public Task<ListViewResponse<TownRaw>> ListTownsAsync(string? titleFilter, CancellationToken ct)
        => ListDictionaryAsync<TownRaw>(VisaryMnemonics.Town, titleFilter, ct);

    public Task<ListViewResponse<RegionRaw>> ListRegionsAsync(string? titleFilter, CancellationToken ct)
        => ListDictionaryAsync<RegionRaw>(VisaryMnemonics.Region, titleFilter, ct);

    public Task<ListViewResponse<ProjectTypeRaw>> ListProjectTypesAsync(CancellationToken ct)
        => ListDictionaryAsync<ProjectTypeRaw>(VisaryMnemonics.ProjectType, null, ct);

    public Task<ListViewResponse<InflationCalcMethodRaw>> ListInflationCalcMethodsAsync(CancellationToken ct)
        => ListDictionaryAsync<InflationCalcMethodRaw>(VisaryMnemonics.InflationCalcMethod, null, ct);

    public Task<ListViewResponse<EstateClassRaw>> ListEstateClassesAsync(CancellationToken ct)
        => ListDictionaryAsync<EstateClassRaw>(VisaryMnemonics.EstateClass, null, ct);

    public Task<ListViewResponse<BuildingMaterialRaw>> ListBuildingMaterialsAsync(CancellationToken ct)
        => ListDictionaryAsync<BuildingMaterialRaw>(VisaryMnemonics.BuildingMaterial, null, ct);

    public Task<ListViewResponse<FinishingMaterialRaw>> ListFinishingMaterialsAsync(CancellationToken ct)
        => ListDictionaryAsync<FinishingMaterialRaw>(VisaryMnemonics.FinishingMaterial, null, ct);

    public Task<ListViewResponse<RoomKindRaw>> ListRoomKindsAsync(CancellationToken ct)
    {
        // Inline вместо ListDictionaryAsync — RoomKind единственный справочник,
        // которому нужны доп. колонки сверх ["ID", "Title", "Hidden"].
        var body = new
        {
            Mnemonic = VisaryMnemonics.RoomKind,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = RoomKindColumns,
            Filter = (object?)null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };
        _log.LogDebug("Visary → GET listview/{Mnemonic} (with RoomCategory)", VisaryMnemonics.RoomKind);
        return PostListViewAsync<RoomKindRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.RoomKind}", body,
            VisaryMnemonics.RoomKind, ct);
    }

    private Task<ListViewResponse<TEntity>> ListDictionaryAsync<TEntity>(
        string mnemonic, string? titleFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = mnemonic,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = DictionaryColumns,
            Filter = titleFilter != null ? FilterByString("Title", titleFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };
        _log.LogDebug("Visary → GET listview/{Mnemonic} (dictionary)", mnemonic);
        return PostListViewAsync<TEntity>(
            $"{BaseUrl}/api/visary/listview/{mnemonic}", body, mnemonic, ct);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<ListViewResponse<TEntity>> PostListViewAsync<TEntity>(
        string url, object body, string logLabel, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<TEntity>>(JsonOptions, ct)
            ?? new ListViewResponse<TEntity>();

        _log.LogInformation("Visary ← 200 {Label}: {Count} rows, total={Total}",
            logLabel, parsed.Data.Count, parsed.Total);
        return parsed;
    }

    // Visary listview ожидает Filter как JSON-массив, упакованный в строку.
    // Сериализуем через JsonSerializer — он сам экранирует кавычки/обратные слэши/Unicode,
    // что закрывает любую возможность инъекции через входное значение фильтра.
    private static string FilterByString(string field, string value)
        => JsonSerializer.Serialize(new object[] { field, "=", value });

    // Visary contains-фильтр: матчит подстроку. Нужен, например, для Title с
    // хвостовыми пробелами в БД (Visary внутри Trim'ит — UI использует contains
    // именно поэтому). Точное "=" в таких случаях не находит запись.
    private static string FilterByStringContains(string field, string value)
        => JsonSerializer.Serialize(new object[] { field, "contains", value });

    private static string FilterByInt(string field, int value)
        => JsonSerializer.Serialize(new object[] { field, "=", value });

    private static string FilterByRefId(string field, int id)
        => JsonSerializer.Serialize(new object[] { field, "=", $"ID:{id}" });

    /// <summary>
    /// Visary listview-фильтр для ссылочного поля через <c>contains "ID:{id}"</c>.
    /// Используется там, где точное "=" не работает (например, поле приходит
    /// объектом {ID, Title} и Visary матчит подстроку). Подсмотрено в реальном
    /// запросе: <c>["Organization","contains","ID:4500"]</c>.
    /// </summary>
    private static string FilterByRefIdContains(string field, int id)
        => JsonSerializer.Serialize(new object[] { field, "contains", $"ID:{id}" });

    // Visary ожидает, что вложенные фильтры — это уже-сериализованные JSON-массивы,
    // которые надо встроить «как есть» в внешний массив. Поэтому склейка через строку,
    // но сами f1/f2 строятся безопасно через JsonSerializer выше.
    private static string FilterAnd(string f1, string f2) => $"[{f1},\"and\",{f2}]";
}
