using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Visary.Api.Common;
using Visary.Api.Dto;

namespace Visary.Api.ListView;

public interface IListViewClient : IDisposable
{
    Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(
        string? search = null, int pageSize = 200, CancellationToken ct = default);

    Task<ListViewResponse<ConstructionProjectRaw>> GetProjectByIdAsync(
        int projectId, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetProjectByIdAsync));

    Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
        int projectId, CancellationToken ct = default);

    Task<ConstructionSiteRaw?> GetSiteByIdAsync(
        int siteId, CancellationToken ct = default);

    Task<ConstructionSiteRaw?> GetSiteByProjectAndIdAsync(
        int projectId, int siteId, CancellationToken ct = default);

    Task<ListViewResponse<ConstructionSiteIndicatorRaw>> GetIndicatorsBySiteAsync(
        int siteId, string? titleFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetIndicatorsBySiteAsync));

    Task<ListViewResponse<ConstructionSiteIndicatorValueRaw>> GetIndicatorValuesByIndicatorAsync(
        int indicatorId, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetIndicatorValuesByIndicatorAsync));

    Task<ListViewResponse<DealRaw>> GetDealsByProjectAsync(
        int projectId, string? lmIdFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetDealsByProjectAsync));

    Task<ListViewResponse<DealRaw>> GetDealsAsync(
        string? lmIdFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetDealsAsync));

    Task<ListViewResponse<OrganizationRaw>> GetOrganizationsByClientIdAsync(
        string clientId, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetOrganizationsByClientIdAsync));

    Task<ListViewResponse<RoomRaw>> GetRoomsBySiteAsync(
        int siteId, string? uniqueNumberFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetRoomsBySiteAsync));

    Task<ListViewResponse<RoomRaw>> GetRoomsBySectionAsync(
        int sectionId, string? uniqueNumberFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetRoomsBySectionAsync));

    Task<ListViewResponse<PercentBetRaw>> GetPercentBetsAsync(
        string? lmIdFilter = null, int? dealId = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetPercentBetsAsync));

    Task<ListViewResponse<ConstructionSectionRaw>> GetSectionsBySiteAsync(
        int siteId, string? titleFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetSectionsBySiteAsync));

    Task<ListViewResponse<ShareAgreementRaw>> GetShareAgreementsByRoomAsync(
        int roomId, string? numberFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetShareAgreementsByRoomAsync));
}

public sealed class ListViewClient : VisaryHttpBase<ListViewClient>, IListViewClient
{
    private static readonly string[] ProjectColumns =
        ["ID", "Title", "IdentifierKK", "IdentifierZPLM", "Hidden"];

    private static readonly string[] ProjectFullColumns =
        ["ID", "Title", "Program", "Author", "ProjectManager", "Executor", "Sponsor",
         "Stage", "Type", "Phase", "Region", "Town", "Date", "Developer", "DeveloperPIN",
         "DeveloperGroup", "IdentifierKK", "IdentifierZPLM", "ConstructionProjectNumber",
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

    public ListViewClient(
        HttpClient http,
        IOptions<VisaryOptions> options,
        ILogger<ListViewClient> log)
        : base(http, options, log) { }

    // ─── Projects ────────────────────────────────────────────────────────────

    public async Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(
        string? search, int pageSize, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionproject",
            PageSkip = 0,
            PageSize = pageSize,
            Columns = ProjectColumns,
            SearchString = search ?? string.Empty,
        };

        _log.LogDebug("Visary → GET listview/constructionproject search='{Search}'", search);
        return await PostListViewAsync<ConstructionProjectRaw>(
            $"{BaseUrl}/api/visary/listview/constructionproject", body, "constructionproject", ct);
    }

    public async Task<ListViewResponse<ConstructionProjectRaw>> GetProjectByIdAsync(
        int projectId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionproject",
            PageSkip = 0,
            PageSize = 1,
            Columns = ProjectFullColumns,
            Filter = FilterByInt("ID", projectId),
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET listview/constructionproject by id={Id}", projectId);
        return await PostListViewAsync<ConstructionProjectRaw>(
            $"{BaseUrl}/api/visary/listview/constructionproject", body, $"constructionproject id={projectId}", ct);
    }

    // ─── Sites ───────────────────────────────────────────────────────────────

    public async Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
        int projectId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionsite",
            PageSkip = 0,
            PageSize = 500,
            Columns = SiteColumns,
            SearchPhrase = (string?)null,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET listview/constructionsite/onetomany/Project projectId={ProjectId}", projectId);
        return await PostListViewAsync<ConstructionSiteRaw>(
            $"{BaseUrl}/api/visary/listview/constructionsite/onetomany/Project?associationId={projectId}",
            body, $"constructionsite/onetomany/Project id={projectId}", ct);
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

    // ─── Indicators (ТЭПы) ───────────────────────────────────────────────────

    public async Task<ListViewResponse<ConstructionSiteIndicatorRaw>> GetIndicatorsBySiteAsync(
        int siteId, string? titleFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionsiteindicator",
            PageSkip = 0,
            PageSize = 500,
            Columns = IndicatorColumns,
            Filter = titleFilter != null ? FilterByString("Title", titleFilter) : (string?)null,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET constructionsiteindicator/onetomany/ConstructionSite siteId={SiteId}", siteId);
        return await PostListViewAsync<ConstructionSiteIndicatorRaw>(
            $"{BaseUrl}/api/visary/listview/constructionsiteindicator/onetomany/ConstructionSite?associationId={siteId}",
            body, $"constructionsiteindicator siteId={siteId}", ct);
    }

    public async Task<ListViewResponse<ConstructionSiteIndicatorValueRaw>> GetIndicatorValuesByIndicatorAsync(
        int indicatorId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionsiteindicatorvalue",
            PageSkip = 0,
            PageSize = 500,
            Columns = IndicatorValueColumns,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET constructionsiteindicatorvalue/onetomany/ConstructionSiteIndicator indicatorId={Id}", indicatorId);
        return await PostListViewAsync<ConstructionSiteIndicatorValueRaw>(
            $"{BaseUrl}/api/visary/listview/constructionsiteindicatorvalue/onetomany/ConstructionSiteIndicator?associationId={indicatorId}",
            body, $"constructionsiteindicatorvalue indicatorId={indicatorId}", ct);
    }

    // ─── Deals ───────────────────────────────────────────────────────────────

    public async Task<ListViewResponse<DealRaw>> GetDealsByProjectAsync(
        int projectId, string? lmIdFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "deal",
            PageSkip = 0,
            PageSize = 500,
            Columns = DealColumns,
            Filter = lmIdFilter != null ? FilterByString("LmID", lmIdFilter) : (string?)null,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET deal/onetomany/ConstructionProject projectId={ProjectId}", projectId);
        return await PostListViewAsync<DealRaw>(
            $"{BaseUrl}/api/visary/listview/deal/onetomany/ConstructionProject?associationId={projectId}",
            body, $"deal/onetomany/ConstructionProject id={projectId}", ct);
    }

    public async Task<ListViewResponse<DealRaw>> GetDealsAsync(
        string? lmIdFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "deal",
            PageSkip = 0,
            PageSize = 50,
            Columns = DealColumns,
            Filter = lmIdFilter != null ? FilterByString("LmID", lmIdFilter) : (string?)null,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET deal lmId='{LmId}'", lmIdFilter);
        return await PostListViewAsync<DealRaw>(
            $"{BaseUrl}/api/visary/listview/deal", body, "deal", ct);
    }

    // ─── Organizations ───────────────────────────────────────────────────────

    public async Task<ListViewResponse<OrganizationRaw>> GetOrganizationsByClientIdAsync(
        string clientId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "organization",
            PageSkip = 0,
            PageSize = 50,
            Columns = OrganizationColumns,
            Filter = FilterByString("ClientID", clientId),
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET organization clientId='{ClientId}'", clientId);
        return await PostListViewAsync<OrganizationRaw>(
            $"{BaseUrl}/api/visary/listview/organization", body, $"organization clientId={clientId}", ct);
    }

    // ─── Rooms ───────────────────────────────────────────────────────────────

    public async Task<ListViewResponse<RoomRaw>> GetRoomsBySiteAsync(
        int siteId, string? uniqueNumberFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "room",
            PageSkip = 0,
            PageSize = 500,
            Columns = RoomColumns,
            Filter = uniqueNumberFilter != null ? FilterByString("UniqueNumber", uniqueNumberFilter) : (string?)null,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET room/onetomany/Site siteId={SiteId}", siteId);
        return await PostListViewAsync<RoomRaw>(
            $"{BaseUrl}/api/visary/listview/room/onetomany/Site?associationId={siteId}",
            body, $"room/onetomany/Site siteId={siteId}", ct);
    }

    public async Task<ListViewResponse<RoomRaw>> GetRoomsBySectionAsync(
        int sectionId, string? uniqueNumberFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "room",
            PageSkip = 0,
            PageSize = 500,
            Columns = RoomColumns,
            Filter = uniqueNumberFilter != null ? FilterByString("UniqueNumber", uniqueNumberFilter) : (string?)null,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET room/onetomany/Section sectionId={SectionId}", sectionId);
        return await PostListViewAsync<RoomRaw>(
            $"{BaseUrl}/api/visary/listview/room/onetomany/Section?associationId={sectionId}",
            body, $"room/onetomany/Section sectionId={sectionId}", ct);
    }

    // ─── PercentBet ──────────────────────────────────────────────────────────

    public async Task<ListViewResponse<PercentBetRaw>> GetPercentBetsAsync(
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
            Mnemonic = "percentbet",
            PageSkip = 0,
            PageSize = 50,
            Columns = PercentBetColumns,
            Filter = filter,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET percentbet lmId='{LmId}' dealId={DealId}", lmIdFilter, dealId);
        return await PostListViewAsync<PercentBetRaw>(
            $"{BaseUrl}/api/visary/listview/percentbet", body, "percentbet", ct);
    }

    // ─── Sections ────────────────────────────────────────────────────────────

    public async Task<ListViewResponse<ConstructionSectionRaw>> GetSectionsBySiteAsync(
        int siteId, string? titleFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionsection",
            PageSkip = 0,
            PageSize = 500,
            Columns = SectionColumns,
            Filter = titleFilter != null ? FilterByString("Title", titleFilter) : (string?)null,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET constructionsection/onetomany/ConstructionSite siteId={SiteId}", siteId);
        return await PostListViewAsync<ConstructionSectionRaw>(
            $"{BaseUrl}/api/visary/listview/constructionsection/onetomany/ConstructionSite?associationId={siteId}",
            body, $"constructionsection siteId={siteId}", ct);
    }

    // ─── ShareAgreements ─────────────────────────────────────────────────────

    public async Task<ListViewResponse<ShareAgreementRaw>> GetShareAgreementsByRoomAsync(
        int roomId, string? numberFilter, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "shareagreement",
            PageSkip = 0,
            PageSize = 50,
            Columns = ShareAgreementColumns,
            Filter = numberFilter != null ? FilterByString("Number", numberFilter) : (string?)null,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        _log.LogDebug("Visary → GET shareagreement/onetomany/Room roomId={RoomId}", roomId);
        return await PostListViewAsync<ShareAgreementRaw>(
            $"{BaseUrl}/api/visary/listview/shareagreement/onetomany/Room?associationId={roomId}",
            body, $"shareagreement roomId={roomId}", ct);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<ListViewResponse<TEntity>> PostListViewAsync<TEntity>(
        string url, object body, string logLabel, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions);

        using var response = await _http.SendAsync(req, ct);
        HandleAuthError(response, ct);
        HandleError(response, ct);

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<TEntity>>(JsonOptions, ct)
            ?? new ListViewResponse<TEntity>();

        _log.LogInformation("Visary ← 200 {Label}: {Count} rows, total={Total}",
            logLabel, parsed.Data.Count, parsed.Total);
        return parsed;
    }

    private static string FilterByString(string field, string value)
        => $"[\"{field}\",\"=\",\"{value}\"]";

    private static string FilterByInt(string field, int value)
        => $"[\"{field}\",\"=\",{value}]";

    private static string FilterByRefId(string field, int id)
        => $"[\"{field}\",\"=\",\"ID:{id}\"]";

    private static string FilterAnd(string f1, string f2)
        => $"[{f1},\"and\",{f2}]";
}
