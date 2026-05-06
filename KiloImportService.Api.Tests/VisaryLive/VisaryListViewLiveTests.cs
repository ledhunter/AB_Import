using Xunit;

namespace KiloImportService.Api.Tests.VisaryLive;

/// <summary>
/// Live-тесты listview-методов. Главная проверка — десериализация полученного JSON в *Raw DTO.
/// Именно тут раньше упали 3 поля (MainSource/RoomCategory/Status) — listview возвращает
/// бОльший объём строк, увеличивая шанс попасть на "плохую" комбинацию типов.
/// </summary>
[Trait("Category", "live")]
public sealed class VisaryListViewLiveTests
{
    private static void SkipIfNoToken()
    {
        var (_, token) = VisaryLiveTestConfig.Resolve();
        Skip.If(string.IsNullOrWhiteSpace(token) || !VisaryLiveTestConfig.IsTokenLikelyAlive(token),
                VisaryLiveTestConfig.SkipReason());
    }

    // ─── Основные сущности ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetProjectsAsync_returns_at_least_one_row()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().GetProjectsAsync(null, 5, default);
        Assert.NotNull(resp);
        Assert.True(resp.Total > 0, "В test-стенде должны быть хоть какие-то проекты");
    }

    [SkippableFact]
    public async Task GetSitesByProjectAsync_returns_sites_for_known_project()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetSitesByProjectAsync(VisaryLiveTestIds.ConstructionProject, default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0, "У известного проекта должны быть сайты");
    }

    [SkippableFact]
    public async Task GetSectionsBySiteAsync_deserializes_without_error()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetSectionsBySiteAsync(VisaryLiveTestIds.ConstructionSite, null, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task GetIndicatorsBySiteAsync_deserializes_without_error()
    {
        // Регрессия: тут раньше падало с MainSource: string vs Number.
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetIndicatorsBySiteAsync(VisaryLiveTestIds.ConstructionSite, null, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task GetIndicatorValuesByIndicatorAsync_deserializes_without_error()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetIndicatorValuesByIndicatorAsync(VisaryLiveTestIds.ConstructionSiteIndicator, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task GetRoomsBySiteAsync_deserializes_without_error()
    {
        // Регрессия: тут раньше падало с RoomCategory: VisaryRef vs Number.
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetRoomsBySiteAsync(VisaryLiveTestIds.ConstructionSite, null, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task ListCadastralAreasAsync_deserializes_without_error()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListCadastralAreasAsync(null, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task GetPercentBetsAsync_deserializes_without_error()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetPercentBetsAsync(null, null, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task GetShareAgreementsByRoomAsync_deserializes_without_error()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetShareAgreementsByRoomAsync(VisaryLiveTestIds.Room, null, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task GetDealsAsync_deserializes_without_error()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().GetDealsAsync(null, default);
        Assert.NotNull(resp);
    }

    [SkippableFact]
    public async Task GetOrganizationsByClientIdAsync_deserializes_without_error()
    {
        // Регрессия: тут раньше падало с Status: string vs Number.
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetOrganizationsByClientIdAsync(VisaryLiveTestIds.OrganizationClientId, default);
        Assert.NotNull(resp);
    }

    // ─── Справочники ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListTownsAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListTownsAsync(null, default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }

    [SkippableFact]
    public async Task ListRegionsAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListRegionsAsync(null, default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }

    [SkippableFact]
    public async Task ListProjectTypesAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListProjectTypesAsync(default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }

    [SkippableFact]
    public async Task ListInflationCalcMethodsAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListInflationCalcMethodsAsync(default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }

    [SkippableFact]
    public async Task ListEstateClassesAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListEstateClassesAsync(default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }

    [SkippableFact]
    public async Task ListBuildingMaterialsAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListBuildingMaterialsAsync(default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }

    [SkippableFact]
    public async Task ListFinishingMaterialsAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListFinishingMaterialsAsync(default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }

    [SkippableFact]
    public async Task ListRoomKindsAsync_deserializes_and_returns_rows()
    {
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView().ListRoomKindsAsync(default);
        Assert.NotNull(resp);
        Assert.True(resp.Data.Count > 0);
    }
}
