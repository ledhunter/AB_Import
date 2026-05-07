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

    Task<ListViewResponse<DealRaw>> GetDealsByProjectAsync(
        int projectId, string? lmIdFilter = null, CancellationToken ct = default);

    Task<ListViewResponse<DealRaw>> GetDealsAsync(
        string? lmIdFilter = null, CancellationToken ct = default);

    Task<ListViewResponse<OrganizationRaw>> GetOrganizationsByClientIdAsync(
        string clientId, CancellationToken ct = default);

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

    Task<ListViewResponse<CadastralAreaFull>> ListCadastralAreasAsync(
        string? cadastralNumFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Список WBS-записей (главы и подстатьи бюджета) у проекта.
    /// Используется для поиска существующей главы по Title/Code перед созданием подстатьи.
    /// </summary>
    Task<ListViewResponse<WbsRaw>> GetWbsByProjectAsync(
        int projectId, CancellationToken ct = default);

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
         "ConstructionProjectNumber", "RegionId", "TownId", "Address",
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

    private static readonly string[] WbsColumns =
        ["ID", "Title", "Code", "ParentID", "Parent", "ProjectID", "Project",
         "ConstructionSite", "DeclaredSum", "ConfirmedSum"];

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
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Site,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = SiteColumns,
            SearchPhrase = (string?)null,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET listview/{Mnemonic}/onetomany/Project projectId={ProjectId}",
            VisaryMnemonics.Site, projectId);
        return PostListViewAsync<ConstructionSiteRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/onetomany/Project?associationId={projectId}",
            body, $"{VisaryMnemonics.Site}/onetomany/Project id={projectId}", ct);
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
        int projectId, string? lmIdFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Deal,
            PageSkip = 0,
            PageSize = Options.LargePageSize,
            Columns = DealColumns,
            Filter = lmIdFilter != null ? FilterByString("LmID", lmIdFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic}/onetomany/ConstructionProject projectId={ProjectId}",
            VisaryMnemonics.Deal, projectId);
        return PostListViewAsync<DealRaw>(
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Deal}/onetomany/ConstructionProject?associationId={projectId}",
            body, $"{VisaryMnemonics.Deal}/onetomany/ConstructionProject id={projectId}", ct);
    }

    public Task<ListViewResponse<DealRaw>> GetDealsAsync(string? lmIdFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Deal,
            PageSkip = 0,
            PageSize = Options.DefaultPageSize,
            Columns = DealColumns,
            Filter = lmIdFilter != null ? FilterByString("LmID", lmIdFilter) : null,
            SearchPhrase = (string?)null,
            Sorts = SortsNullSentinel,
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET {Mnemonic} lmId='{LmId}'", VisaryMnemonics.Deal, lmIdFilter);
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
        => ListDictionaryAsync<RoomKindRaw>(VisaryMnemonics.RoomKind, null, ct);

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

    // Visary ожидает, что вложенные фильтры — это уже-сериализованные JSON-массивы,
    // которые надо встроить «как есть» в внешний массив. Поэтому склейка через строку,
    // но сами f1/f2 строятся безопасно через JsonSerializer выше.
    private static string FilterAnd(string f1, string f2) => $"[{f1},\"and\",{f2}]";
}
