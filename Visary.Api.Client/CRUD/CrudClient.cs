using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Visary.Api.Common;
using Visary.Api.Dto;

namespace Visary.Api.CRUD;

public interface ICrudClient : IDisposable
{
    Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId, int finishingMaterialId, CancellationToken ct = default);

    Task<bool> PatchSiteAsync(
        int siteId, SitePatchRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(PatchSiteAsync));

    Task<ConstructionSiteRaw> CreateSiteAsync(
        SiteCreateRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(CreateSiteAsync));

    Task<bool> PatchProjectAsync(
        int projectId, ProjectPatchRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(PatchProjectAsync));

    Task<ConstructionProjectRaw> CreateProjectAsync(
        ProjectCreateRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(CreateProjectAsync));

    Task<bool> PatchIndicatorValueAsync(
        int valueId, IndicatorValuePatchRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(PatchIndicatorValueAsync));

    Task<CadastralAreaRaw> CreateCadastralAreaAsync(
        CadastralAreaCreateRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(CreateCadastralAreaAsync));

    Task<bool> PatchCadastralAreaAsync(
        int areaId, CadastralAreaPatchRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(PatchCadastralAreaAsync));

    Task<bool> LinkCadastralAreaToSiteAsync(
        int siteId, int areaId, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(LinkCadastralAreaToSiteAsync));

    Task<PercentBetRaw> CreatePercentBetAsync(
        PercentBetCreateRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(CreatePercentBetAsync));

    Task<ConstructionSectionRaw> CreateSectionAsync(
        SectionCreateRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(CreateSectionAsync));

    Task<RoomRaw> CreateRoomAsync(
        RoomCreateRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(CreateRoomAsync));

    Task<ShareAgreementRaw> CreateShareAgreementAsync(
        ShareAgreementCreateRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(nameof(CreateShareAgreementAsync));
}

public sealed class CrudClient : VisaryHttpBase<CrudClient>, ICrudClient
{
    public CrudClient(
        HttpClient http,
        IOptions<VisaryOptions> options,
        ILogger<CrudClient> log)
        : base(http, options, log) { }

    // ─── ConstructionSite ────────────────────────────────────────────────────

    public async Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId, int finishingMaterialId, CancellationToken ct)
    {
        var siteData = await FetchSiteForUpdateAsync(siteId, ct);
        if (siteData == null)
            throw new KeyNotFoundException($"ConstructionSite с ID={siteId} не найден в Visary");

        siteData.FinishingMaterialId = finishingMaterialId;
        await LegacyUpdateSiteAsync(siteData, ct);

        _log.LogInformation("CrudClient.UpdateSiteFinishingMaterialAsync: siteId={SiteId} success", siteId);
        return true;
    }

    public async Task<bool> PatchSiteAsync(
        int siteId, SitePatchRequest request, CancellationToken ct)
    {
        request.ID = siteId;
        _log.LogDebug("Visary → PATCH constructionsite id={Id}", siteId);
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/constructionsite/{siteId}?forceUpdate=false",
            request, $"constructionsite/{siteId}", ct);
        _log.LogInformation("CrudClient.PatchSiteAsync: siteId={SiteId} success", siteId);
        return true;
    }

    public async Task<ConstructionSiteRaw> CreateSiteAsync(
        SiteCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST constructionsite");
        var result = await PostCrudAsync<ConstructionSiteRaw>(
            $"{BaseUrl}/api/visary/crud/constructionsite", request, "constructionsite", ct);
        _log.LogInformation("CrudClient.CreateSiteAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionProject ─────────────────────────────────────────────────

    public async Task<bool> PatchProjectAsync(
        int projectId, ProjectPatchRequest request, CancellationToken ct)
    {
        request.ID = projectId;
        _log.LogDebug("Visary → PATCH constructionproject id={Id}", projectId);
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/constructionproject/{projectId}?forceUpdate=false",
            request, $"constructionproject/{projectId}", ct);
        _log.LogInformation("CrudClient.PatchProjectAsync: projectId={ProjectId} success", projectId);
        return true;
    }

    public async Task<ConstructionProjectRaw> CreateProjectAsync(
        ProjectCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST constructionproject");
        var result = await PostCrudAsync<ConstructionProjectRaw>(
            $"{BaseUrl}/api/visary/crud/constructionproject", request, "constructionproject", ct);
        _log.LogInformation("CrudClient.CreateProjectAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionSiteIndicatorValue (ТЭП) ────────────────────────────────

    public async Task<bool> PatchIndicatorValueAsync(
        int valueId, IndicatorValuePatchRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → PATCH constructionsiteindicatorvalue id={Id}", valueId);
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/constructionsiteindicatorvalue/{valueId}?forceUpdate=true",
            request, $"constructionsiteindicatorvalue/{valueId}", ct);
        _log.LogInformation("CrudClient.PatchIndicatorValueAsync: valueId={ValueId} success", valueId);
        return true;
    }

    // ─── CadastralArea (ЗУ) ──────────────────────────────────────────────────

    public async Task<CadastralAreaRaw> CreateCadastralAreaAsync(
        CadastralAreaCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST cadastralarea");
        var result = await PostCrudAsync<CadastralAreaRaw>(
            $"{BaseUrl}/api/visary/crud/cadastralarea", request, "cadastralarea", ct);
        _log.LogInformation("CrudClient.CreateCadastralAreaAsync: created id={Id}", result.ID);
        return result;
    }

    public async Task<bool> PatchCadastralAreaAsync(
        int areaId, CadastralAreaPatchRequest request, CancellationToken ct)
    {
        request.ID = areaId;
        _log.LogDebug("Visary → PATCH cadastralarea id={Id}", areaId);
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/cadastralarea/{areaId}?forceUpdate=false",
            request, $"cadastralarea/{areaId}", ct);
        _log.LogInformation("CrudClient.PatchCadastralAreaAsync: areaId={AreaId} success", areaId);
        return true;
    }

    public async Task<bool> LinkCadastralAreaToSiteAsync(
        int siteId, int areaId, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST constructionsite/manytomany/cadastralarea/link siteId={SiteId} areaId={AreaId}", siteId, areaId);
        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/constructionsite/manytomany/cadastralarea/link?associationId={siteId}&ids={areaId}");
        using var response = await _http.SendAsync(req, ct);
        HandleAuthError(response, ct);
        HandleError(response, ct);
        _log.LogInformation("CrudClient.LinkCadastralAreaToSiteAsync: siteId={SiteId} areaId={AreaId} success", siteId, areaId);
        return true;
    }

    // ─── PercentBet ──────────────────────────────────────────────────────────

    public async Task<PercentBetRaw> CreatePercentBetAsync(
        PercentBetCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST percentbet");
        var result = await PostCrudAsync<PercentBetRaw>(
            $"{BaseUrl}/api/visary/crud/percentbet", request, "percentbet", ct);
        _log.LogInformation("CrudClient.CreatePercentBetAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionSection ─────────────────────────────────────────────────

    public async Task<ConstructionSectionRaw> CreateSectionAsync(
        SectionCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST constructionsection");
        var result = await PostCrudAsync<ConstructionSectionRaw>(
            $"{BaseUrl}/api/visary/crud/constructionsection", request, "constructionsection", ct);
        _log.LogInformation("CrudClient.CreateSectionAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── Room ────────────────────────────────────────────────────────────────

    public async Task<RoomRaw> CreateRoomAsync(
        RoomCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST room");
        var result = await PostCrudAsync<RoomRaw>(
            $"{BaseUrl}/api/visary/crud/room", request, "room", ct);
        _log.LogInformation("CrudClient.CreateRoomAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ShareAgreement (ДДУ) ────────────────────────────────────────────────

    public async Task<ShareAgreementRaw> CreateShareAgreementAsync(
        ShareAgreementCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST shareagreement");
        var result = await PostCrudAsync<ShareAgreementRaw>(
            $"{BaseUrl}/api/visary/crud/shareagreement", request, "shareagreement", ct);
        _log.LogInformation("CrudClient.CreateShareAgreementAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<TEntity> PostCrudAsync<TEntity>(
        string url, object body, string logLabel, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions);
        using var response = await _http.SendAsync(req, ct);
        HandleAuthError(response, ct);
        HandleError(response, ct);
        var result = await response.Content.ReadFromJsonAsync<TEntity>(JsonOptions, ct);
        _log.LogInformation("Visary ← 200 POST {Label}", logLabel);
        return result!;
    }

    private async Task PatchCrudAsync(string url, object body, string logLabel, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Patch, url);
        req.Content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions);
        using var response = await _http.SendAsync(req, ct);
        HandleAuthError(response, ct);
        HandleConflict(response, ct, logLabel);
        HandleError(response, ct);
        _log.LogInformation("Visary ← 200 PATCH {Label}", logLabel);
    }

    // ─── Legacy: UpdateSiteFinishingMaterialAsync internals ──────────────────

    private async Task<SiteUpdateData?> FetchSiteForUpdateAsync(int siteId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionsite",
            PageSkip = 0,
            PageSize = 1,
            Columns = new[] { "ID", "FinishingMaterialId", "Version" },
            AssociatedID = siteId,
        };

        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/constructionsite");
        req.Content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions);

        _log.LogDebug("Visary → GET constructionsite by ID={SiteId}", siteId);
        using var response = await _http.SendAsync(req, ct);
        HandleAuthError(response, ct);
        HandleError(response, ct);

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<SiteUpdateData>>(JsonOptions, ct)
            ?? new ListViewResponse<SiteUpdateData>();

        if (parsed.Data.Count == 0)
        {
            _log.LogWarning("Visary ← 200 constructionsite siteId={SiteId}: no rows", siteId);
            return null;
        }

        _log.LogInformation("Visary ← 200 constructionsite siteId={SiteId}: 1 row", siteId);
        return parsed.Data[0];
    }

    private async Task LegacyUpdateSiteAsync(SiteUpdateData siteData, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "constructionsite",
            Data = new[] { siteData },
        };

        using var req = NewRequest(HttpMethod.Put,
            $"{BaseUrl}/api/visary/listview/constructionsite");
        req.Content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions);

        _log.LogDebug("Visary → PUT constructionsite ID={SiteId}", siteData.ID);
        using var response = await _http.SendAsync(req, ct);
        HandleAuthError(response, ct);
        HandleConflict(response, ct, $"constructionsite/{siteData.ID}");
        HandleError(response, ct);
        _log.LogInformation("Visary ← 200 PUT constructionsite ID={SiteId}", siteData.ID);
    }

    private sealed class SiteUpdateData
    {
        public int ID { get; set; }
        public int? FinishingMaterialId { get; set; }
        public DateTime? Version { get; set; }
    }
}
