using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Visary.Api.Common;
using Visary.Api.Dto;

namespace Visary.Api.CRUD;

public interface ICrudClient
{
    // ─── Update / Create / Patch / Link (модифицирующие) ─────────────────────
    Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId, int finishingMaterialId, CancellationToken ct = default);

    Task<bool> PatchSiteAsync(
        int siteId, SitePatchRequest request, CancellationToken ct = default);

    Task<ConstructionSiteRaw> CreateSiteAsync(
        SiteCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchProjectAsync(
        int projectId, ProjectPatchRequest request, CancellationToken ct = default);

    Task<ConstructionProjectRaw> CreateProjectAsync(
        ProjectCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchIndicatorValueAsync(
        int valueId, IndicatorValuePatchRequest request, CancellationToken ct = default);

    Task<CadastralAreaRaw> CreateCadastralAreaAsync(
        CadastralAreaCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchCadastralAreaAsync(
        int areaId, CadastralAreaPatchRequest request, CancellationToken ct = default);

    Task<bool> LinkCadastralAreaToSiteAsync(
        int siteId, int areaId, CancellationToken ct = default);

    Task<PercentBetRaw> CreatePercentBetAsync(
        PercentBetCreateRequest request, CancellationToken ct = default);

    Task<ConstructionSectionRaw> CreateSectionAsync(
        SectionCreateRequest request, CancellationToken ct = default);

    Task<RoomRaw> CreateRoomAsync(
        RoomCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchRoomAsync(
        int roomId, RoomPatchRequest request, CancellationToken ct = default);

    Task<ShareAgreementRaw> CreateShareAgreementAsync(
        ShareAgreementCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchShareAgreementAsync(
        int shareAgreementId, ShareAgreementPatchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Привязывает Organization участником Site. Соответствует шагу из puml
    /// «Добавить в Участники Объекта найденную Организацию с ролью Застройщик».
    /// Реализован через manytomany-link, как и <see cref="LinkCadastralAreaToSiteAsync"/>.
    /// ⚠️ Backend Visary ставит роль на основе типа связи; если роль на стенде задаётся
    /// иначе (отдельной сущностью ParticipantRole), потребуется дополнительный вызов.
    /// </summary>
    Task<bool> LinkOrganizationToSiteAsync(
        int siteId, int organizationId, CancellationToken ct = default);

    // ─── GET by ID (чтение, через /api/visary/crud/{mnemonic}/{id}) ──────────
    // Возвращают *Full DTO с полным набором полей сущности — в отличие от
    // listview-методов, которые возвращают только подмножество, явно перечисленное в Columns[].
    Task<TEntity> GetByIdAsync<TEntity>(string mnemonic, int id, CancellationToken ct = default);

    Task<ConstructionProjectFull>            GetProjectByIdFullAsync(int id, CancellationToken ct = default);
    Task<ConstructionSiteFull>               GetSiteByIdFullAsync(int id, CancellationToken ct = default);
    Task<ConstructionSectionFull>            GetSectionByIdAsync(int id, CancellationToken ct = default);
    Task<ConstructionSiteIndicatorFull>      GetIndicatorByIdAsync(int id, CancellationToken ct = default);
    Task<ConstructionSiteIndicatorValueFull> GetIndicatorValueByIdAsync(int id, CancellationToken ct = default);
    Task<RoomFull>                           GetRoomByIdAsync(int id, CancellationToken ct = default);
    Task<CadastralAreaFull>                  GetCadastralAreaByIdAsync(int id, CancellationToken ct = default);
    Task<PercentBetFull>                     GetPercentBetByIdAsync(int id, CancellationToken ct = default);
    Task<ShareAgreementFull>                 GetShareAgreementByIdAsync(int id, CancellationToken ct = default);
    Task<DealFull>                           GetDealByIdAsync(int id, CancellationToken ct = default);
    Task<OrganizationFull>                   GetOrganizationByIdAsync(int id, CancellationToken ct = default);

    // ─── GET by ID для справочников ──────────────────────────────────────────
    Task<TownRaw>                GetTownByIdAsync(int id, CancellationToken ct = default);
    Task<RegionRaw>              GetRegionByIdAsync(int id, CancellationToken ct = default);
    Task<ProjectTypeRaw>         GetProjectTypeByIdAsync(int id, CancellationToken ct = default);
    Task<InflationCalcMethodRaw> GetInflationCalcMethodByIdAsync(int id, CancellationToken ct = default);
    Task<EstateClassRaw>         GetEstateClassByIdAsync(int id, CancellationToken ct = default);
    Task<BuildingMaterialRaw>    GetBuildingMaterialByIdAsync(int id, CancellationToken ct = default);
    Task<FinishingMaterialRaw>   GetFinishingMaterialByIdAsync(int id, CancellationToken ct = default);
    Task<RoomKindRaw>            GetRoomKindByIdAsync(int id, CancellationToken ct = default);
}

public sealed class CrudClient : VisaryHttpBase<CrudClient>, ICrudClient
{
    public CrudClient(
        HttpClient http,
        IOptionsMonitor<VisaryOptions> options,
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
        await PutSiteFullAsync(siteData, ct);

        _log.LogInformation("CrudClient.UpdateSiteFinishingMaterialAsync: siteId={SiteId} success", siteId);
        return true;
    }

    public Task<bool> PatchSiteAsync(int siteId, SitePatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, siteId, r => r.ID, (r, v) => r.ID = v, nameof(siteId));
        _log.LogDebug("Visary → PATCH constructionsite id={Id}", siteId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
            request, $"{VisaryMnemonics.Site}/{siteId}", siteId, ct,
            $"CrudClient.PatchSiteAsync: siteId={{Id}} success");
    }

    public async Task<ConstructionSiteRaw> CreateSiteAsync(SiteCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Site);
        var result = await PostCrudAsync<ConstructionSiteRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}", request, VisaryMnemonics.Site, ct);
        _log.LogInformation("CrudClient.CreateSiteAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionProject ─────────────────────────────────────────────────

    public Task<bool> PatchProjectAsync(int projectId, ProjectPatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, projectId, r => r.ID, (r, v) => r.ID = v, nameof(projectId));
        _log.LogDebug("Visary → PATCH constructionproject id={Id}", projectId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Project}/{projectId}?forceUpdate=false",
            request, $"{VisaryMnemonics.Project}/{projectId}", projectId, ct,
            $"CrudClient.PatchProjectAsync: projectId={{Id}} success");
    }

    public async Task<ConstructionProjectRaw> CreateProjectAsync(ProjectCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Project);
        var result = await PostCrudAsync<ConstructionProjectRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Project}", request, VisaryMnemonics.Project, ct);
        _log.LogInformation("CrudClient.CreateProjectAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionSiteIndicatorValue (ТЭП) ────────────────────────────────

    public async Task<bool> PatchIndicatorValueAsync(
        int valueId, IndicatorValuePatchRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.SiteIndicatorValue, valueId);
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.SiteIndicatorValue}/{valueId}?forceUpdate=true",
            request, $"{VisaryMnemonics.SiteIndicatorValue}/{valueId}", ct);
        _log.LogInformation("CrudClient.PatchIndicatorValueAsync: valueId={ValueId} success", valueId);
        return true;
    }

    // ─── CadastralArea (ЗУ) ──────────────────────────────────────────────────

    public async Task<CadastralAreaRaw> CreateCadastralAreaAsync(
        CadastralAreaCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.CadastralArea);
        var result = await PostCrudAsync<CadastralAreaRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.CadastralArea}", request, VisaryMnemonics.CadastralArea, ct);
        _log.LogInformation("CrudClient.CreateCadastralAreaAsync: created id={Id}", result.ID);
        return result;
    }

    public Task<bool> PatchCadastralAreaAsync(
        int areaId, CadastralAreaPatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, areaId, r => r.ID, (r, v) => r.ID = v, nameof(areaId));
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.CadastralArea, areaId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.CadastralArea}/{areaId}?forceUpdate=false",
            request, $"{VisaryMnemonics.CadastralArea}/{areaId}", areaId, ct,
            $"CrudClient.PatchCadastralAreaAsync: areaId={{Id}} success");
    }

    public async Task<bool> LinkCadastralAreaToSiteAsync(int siteId, int areaId, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}/manytomany/{Linked}/link siteId={SiteId} areaId={AreaId}",
            VisaryMnemonics.Site, VisaryMnemonics.CadastralArea, siteId, areaId);
        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/manytomany/{VisaryMnemonics.CadastralArea}/link?associationId={siteId}&ids={areaId}");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        _log.LogInformation("CrudClient.LinkCadastralAreaToSiteAsync: siteId={SiteId} areaId={AreaId} success", siteId, areaId);
        return true;
    }

    // ─── PercentBet ──────────────────────────────────────────────────────────

    public async Task<PercentBetRaw> CreatePercentBetAsync(PercentBetCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.PercentBet);
        var result = await PostCrudAsync<PercentBetRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.PercentBet}", request, VisaryMnemonics.PercentBet, ct);
        _log.LogInformation("CrudClient.CreatePercentBetAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionSection ─────────────────────────────────────────────────

    public async Task<ConstructionSectionRaw> CreateSectionAsync(SectionCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Section);
        var result = await PostCrudAsync<ConstructionSectionRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Section}", request, VisaryMnemonics.Section, ct);
        _log.LogInformation("CrudClient.CreateSectionAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── Room ────────────────────────────────────────────────────────────────

    public async Task<RoomRaw> CreateRoomAsync(RoomCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Room);
        var result = await PostCrudAsync<RoomRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Room}", request, VisaryMnemonics.Room, ct);
        _log.LogInformation("CrudClient.CreateRoomAsync: created id={Id}", result.ID);
        return result;
    }

    /// <summary>
    /// PATCH Room. Используется <c>forceUpdate=true</c>, чтобы импорт не требовал
    /// предварительной выборки RoomFull ради RowVersion (по аналогии с PATCH ТЭП).
    /// </summary>
    public Task<bool> PatchRoomAsync(int roomId, RoomPatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, roomId, r => r.ID, (r, v) => r.ID = v, nameof(roomId));
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.Room, roomId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Room}/{roomId}?forceUpdate=true",
            request, $"{VisaryMnemonics.Room}/{roomId}", roomId, ct,
            $"CrudClient.PatchRoomAsync: roomId={{Id}} success");
    }

    // ─── ShareAgreement (ДДУ) ────────────────────────────────────────────────

    public async Task<ShareAgreementRaw> CreateShareAgreementAsync(
        ShareAgreementCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.ShareAgreement);
        var result = await PostCrudAsync<ShareAgreementRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.ShareAgreement}", request, VisaryMnemonics.ShareAgreement, ct);
        _log.LogInformation("CrudClient.CreateShareAgreementAsync: created id={Id}", result.ID);
        return result;
    }

    /// <summary>
    /// PATCH ShareAgreement (ДДУ). Используется <c>forceUpdate=true</c>, чтобы избежать
    /// дополнительной выборки ShareAgreementFull ради RowVersion при импорте.
    /// </summary>
    public Task<bool> PatchShareAgreementAsync(
        int shareAgreementId, ShareAgreementPatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, shareAgreementId, r => r.ID, (r, v) => r.ID = v, nameof(shareAgreementId));
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.ShareAgreement, shareAgreementId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.ShareAgreement}/{shareAgreementId}?forceUpdate=true",
            request, $"{VisaryMnemonics.ShareAgreement}/{shareAgreementId}", shareAgreementId, ct,
            $"CrudClient.PatchShareAgreementAsync: shareAgreementId={{Id}} success");
    }

    // ─── Organization ↔ Site link ────────────────────────────────────────────

    public async Task<bool> LinkOrganizationToSiteAsync(int siteId, int organizationId, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}/manytomany/{Linked}/link siteId={SiteId} orgId={OrgId}",
            VisaryMnemonics.Site, VisaryMnemonics.Organization, siteId, organizationId);
        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/manytomany/{VisaryMnemonics.Organization}/link?associationId={siteId}&ids={organizationId}");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        _log.LogInformation("CrudClient.LinkOrganizationToSiteAsync: siteId={SiteId} orgId={OrgId} success",
            siteId, organizationId);
        return true;
    }

    // ─── GET by ID (полные DTO через /crud/{mnemonic}/{id}) ──────────────────

    public Task<TEntity> GetByIdAsync<TEntity>(string mnemonic, int id, CancellationToken ct)
        => GetCrudByIdAsync<TEntity>(mnemonic, id, ct);

    public Task<ConstructionProjectFull>            GetProjectByIdFullAsync(int id, CancellationToken ct)             => GetCrudByIdAsync<ConstructionProjectFull>(VisaryMnemonics.Project, id, ct);
    public Task<ConstructionSiteFull>               GetSiteByIdFullAsync(int id, CancellationToken ct)                => GetCrudByIdAsync<ConstructionSiteFull>(VisaryMnemonics.Site, id, ct);
    public Task<ConstructionSectionFull>            GetSectionByIdAsync(int id, CancellationToken ct)                 => GetCrudByIdAsync<ConstructionSectionFull>(VisaryMnemonics.Section, id, ct);
    public Task<ConstructionSiteIndicatorFull>      GetIndicatorByIdAsync(int id, CancellationToken ct)               => GetCrudByIdAsync<ConstructionSiteIndicatorFull>(VisaryMnemonics.SiteIndicator, id, ct);
    public Task<ConstructionSiteIndicatorValueFull> GetIndicatorValueByIdAsync(int id, CancellationToken ct)          => GetCrudByIdAsync<ConstructionSiteIndicatorValueFull>(VisaryMnemonics.SiteIndicatorValue, id, ct);
    public Task<RoomFull>                           GetRoomByIdAsync(int id, CancellationToken ct)                    => GetCrudByIdAsync<RoomFull>(VisaryMnemonics.Room, id, ct);
    public Task<CadastralAreaFull>                  GetCadastralAreaByIdAsync(int id, CancellationToken ct)           => GetCrudByIdAsync<CadastralAreaFull>(VisaryMnemonics.CadastralArea, id, ct);
    public Task<PercentBetFull>                     GetPercentBetByIdAsync(int id, CancellationToken ct)              => GetCrudByIdAsync<PercentBetFull>(VisaryMnemonics.PercentBet, id, ct);
    public Task<ShareAgreementFull>                 GetShareAgreementByIdAsync(int id, CancellationToken ct)          => GetCrudByIdAsync<ShareAgreementFull>(VisaryMnemonics.ShareAgreement, id, ct);
    public Task<DealFull>                           GetDealByIdAsync(int id, CancellationToken ct)                    => GetCrudByIdAsync<DealFull>(VisaryMnemonics.Deal, id, ct);
    public Task<OrganizationFull>                   GetOrganizationByIdAsync(int id, CancellationToken ct)            => GetCrudByIdAsync<OrganizationFull>(VisaryMnemonics.Organization, id, ct);

    public Task<TownRaw>                GetTownByIdAsync(int id, CancellationToken ct)                => GetCrudByIdAsync<TownRaw>(VisaryMnemonics.Town, id, ct);
    public Task<RegionRaw>              GetRegionByIdAsync(int id, CancellationToken ct)              => GetCrudByIdAsync<RegionRaw>(VisaryMnemonics.Region, id, ct);
    public Task<ProjectTypeRaw>         GetProjectTypeByIdAsync(int id, CancellationToken ct)         => GetCrudByIdAsync<ProjectTypeRaw>(VisaryMnemonics.ProjectType, id, ct);
    public Task<InflationCalcMethodRaw> GetInflationCalcMethodByIdAsync(int id, CancellationToken ct) => GetCrudByIdAsync<InflationCalcMethodRaw>(VisaryMnemonics.InflationCalcMethod, id, ct);
    public Task<EstateClassRaw>         GetEstateClassByIdAsync(int id, CancellationToken ct)         => GetCrudByIdAsync<EstateClassRaw>(VisaryMnemonics.EstateClass, id, ct);
    public Task<BuildingMaterialRaw>    GetBuildingMaterialByIdAsync(int id, CancellationToken ct)    => GetCrudByIdAsync<BuildingMaterialRaw>(VisaryMnemonics.BuildingMaterial, id, ct);
    public Task<FinishingMaterialRaw>   GetFinishingMaterialByIdAsync(int id, CancellationToken ct)   => GetCrudByIdAsync<FinishingMaterialRaw>(VisaryMnemonics.FinishingMaterial, id, ct);
    public Task<RoomKindRaw>            GetRoomKindByIdAsync(int id, CancellationToken ct)            => GetCrudByIdAsync<RoomKindRaw>(VisaryMnemonics.RoomKind, id, ct);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // Caller передал ID в URL и в DTO. Если ID в DTO ненулевой и не совпадает — это
    // ошибка вызывающего, лучше упасть громко, чем тихо переписать поле.
    private static void ApplyEntityId<TRequest>(
        TRequest request, int routeId,
        Func<TRequest, int> getter, Action<TRequest, int> setter,
        string routeParamName)
    {
        var bodyId = getter(request);
        if (bodyId != 0 && bodyId != routeId)
            throw new ArgumentException(
                $"request.ID={bodyId} не совпадает с {routeParamName}={routeId}", nameof(request));
        setter(request, routeId);
    }

    private async Task<bool> PatchAndReportAsync(
        string url, object body, string logLabel, int id,
        CancellationToken ct, string successTemplate)
    {
        await PatchCrudAsync(url, body, logLabel, ct);
        _log.LogInformation(successTemplate, id);
        return true;
    }

    private async Task<TEntity> PostCrudAsync<TEntity>(
        string url, object body, string logLabel, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<TEntity>(JsonOptions, ct);
        _log.LogInformation("Visary ← 200 POST {Label}", logLabel);
        return result!;
    }

    private async Task PatchCrudAsync(string url, object body, string logLabel, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Patch, url);
        req.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleConflictAsync(response, ct, logLabel);
        await HandleErrorAsync(response, ct);
        _log.LogInformation("Visary ← 200 PATCH {Label}", logLabel);
    }

    // ─── UpdateSiteFinishingMaterialAsync internals (PUT через listview) ─────
    // Используется legacy UI-flow: получает строку через listview и шлёт PUT с тем же
    // массивом. Сохраняем как есть — PATCH через /crud/ требует RowVersion (long), а
    // listview отдаёт Version (DateTime); миграция небезопасна без проверки на стенде.

    private async Task<SiteUpdateData?> FetchSiteForUpdateAsync(int siteId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Site,
            PageSkip = 0,
            PageSize = 1,
            Columns = new[] { "ID", "FinishingMaterialId", "Version" },
            AssociatedID = siteId,
        };

        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}");
        req.Content = JsonContent.Create(body, options: JsonOptions);

        _log.LogDebug("Visary → GET {Mnemonic} by ID={SiteId}", VisaryMnemonics.Site, siteId);
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<SiteUpdateData>>(JsonOptions, ct)
            ?? new ListViewResponse<SiteUpdateData>();

        if (parsed.Data.Count == 0)
        {
            _log.LogWarning("Visary ← 200 {Mnemonic} siteId={SiteId}: no rows", VisaryMnemonics.Site, siteId);
            return null;
        }

        _log.LogInformation("Visary ← 200 {Mnemonic} siteId={SiteId}: 1 row", VisaryMnemonics.Site, siteId);
        return parsed.Data[0];
    }

    private async Task PutSiteFullAsync(SiteUpdateData siteData, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = VisaryMnemonics.Site,
            Data = new[] { siteData },
        };

        using var req = NewRequest(HttpMethod.Put,
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}");
        req.Content = JsonContent.Create(body, options: JsonOptions);

        _log.LogDebug("Visary → PUT {Mnemonic} ID={SiteId}", VisaryMnemonics.Site, siteData.ID);
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleConflictAsync(response, ct, $"{VisaryMnemonics.Site}/{siteData.ID}");
        await HandleErrorAsync(response, ct);
        _log.LogInformation("Visary ← 200 PUT {Mnemonic} ID={SiteId}", VisaryMnemonics.Site, siteData.ID);
    }

    private sealed class SiteUpdateData
    {
        public int ID { get; set; }
        public int? FinishingMaterialId { get; set; }
        public DateTime? Version { get; set; }
    }
}
