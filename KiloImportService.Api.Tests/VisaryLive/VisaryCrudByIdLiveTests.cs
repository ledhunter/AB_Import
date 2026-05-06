using Visary.Api.Dto;
using Xunit;

namespace KiloImportService.Api.Tests.VisaryLive;

/// <summary>
/// Live-тесты <c>GET /api/visary/crud/{mnemonic}/{id}</c> для всех 19 мнемоник.
/// Что проверяем: ответ 200, JSON десериализуется в *Full/Raw DTO без падения,
/// возвращённый ID совпадает с запрошенным. Это ловит:
///   - изменения формата ответа Visary (новые поля несовместимого типа)
///   - случайное переименование URL/мнемоники
///   - регрессии в DTO (как с MainSource/RoomCategory/Status, исправленные в #53)
///
/// Тесты помечены trait="live" — фильтруются: <c>dotnet test --filter Category=live</c>.
/// Без живого токена в .audit/.token (или env VISARY_TEST_TOKEN) автоматически skip-аются.
/// </summary>
[Trait("Category", "live")]
public sealed class VisaryCrudByIdLiveTests
{
    [SkippableFact]
    public async Task GetProjectByIdFullAsync_returns_known_project()
    {
        SkipIfNoToken();
        var client = VisaryLiveClientFactory.NewCrud();
        var dto = await client.GetProjectByIdFullAsync(VisaryLiveTestIds.ConstructionProject, default);
        Assert.Equal(VisaryLiveTestIds.ConstructionProject, dto.ID);
        Assert.False(string.IsNullOrWhiteSpace(dto.Title), "Title не должен быть пустым");
    }

    [SkippableFact]
    public async Task GetSiteByIdFullAsync_returns_known_site()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetSiteByIdFullAsync(VisaryLiveTestIds.ConstructionSite, default);
        Assert.Equal(VisaryLiveTestIds.ConstructionSite, dto.ID);
    }

    [SkippableFact]
    public async Task GetSectionByIdAsync_returns_known_section()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetSectionByIdAsync(VisaryLiveTestIds.ConstructionSection, default);
        Assert.Equal(VisaryLiveTestIds.ConstructionSection, dto.ID);
    }

    [SkippableFact]
    public async Task GetIndicatorByIdAsync_returns_known_indicator()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetIndicatorByIdAsync(VisaryLiveTestIds.ConstructionSiteIndicator, default);
        Assert.Equal(VisaryLiveTestIds.ConstructionSiteIndicator, dto.ID);
    }

    [SkippableFact]
    public async Task GetIndicatorValueByIdAsync_returns_known_value()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetIndicatorValueByIdAsync(VisaryLiveTestIds.ConstructionSiteIndicatorValue, default);
        Assert.Equal(VisaryLiveTestIds.ConstructionSiteIndicatorValue, dto.ID);
    }

    [SkippableFact]
    public async Task GetRoomByIdAsync_returns_known_room()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetRoomByIdAsync(VisaryLiveTestIds.Room, default);
        Assert.Equal(VisaryLiveTestIds.Room, dto.ID);
    }

    [SkippableFact]
    public async Task GetCadastralAreaByIdAsync_returns_known_area()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetCadastralAreaByIdAsync(VisaryLiveTestIds.CadastralArea, default);
        Assert.Equal(VisaryLiveTestIds.CadastralArea, dto.ID);
    }

    [SkippableFact]
    public async Task GetPercentBetByIdAsync_returns_known_bet()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetPercentBetByIdAsync(VisaryLiveTestIds.PercentBet, default);
        Assert.Equal(VisaryLiveTestIds.PercentBet, dto.ID);
    }

    [SkippableFact]
    public async Task GetShareAgreementByIdAsync_returns_known_agreement()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetShareAgreementByIdAsync(VisaryLiveTestIds.ShareAgreement, default);
        Assert.Equal(VisaryLiveTestIds.ShareAgreement, dto.ID);
    }

    [SkippableFact]
    public async Task GetDealByIdAsync_returns_known_deal()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetDealByIdAsync(VisaryLiveTestIds.Deal, default);
        Assert.Equal(VisaryLiveTestIds.Deal, dto.ID);
    }

    [SkippableFact]
    public async Task GetOrganizationByIdAsync_returns_known_org()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetOrganizationByIdAsync(VisaryLiveTestIds.Organization, default);
        Assert.Equal(VisaryLiveTestIds.Organization, dto.ID);
    }

    [SkippableFact]
    public async Task GetTownByIdAsync_returns_known_town()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetTownByIdAsync(VisaryLiveTestIds.Town, default);
        Assert.Equal(VisaryLiveTestIds.Town, dto.ID);
    }

    [SkippableFact]
    public async Task GetRegionByIdAsync_returns_known_region()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetRegionByIdAsync(VisaryLiveTestIds.Region, default);
        Assert.Equal(VisaryLiveTestIds.Region, dto.ID);
    }

    [SkippableFact]
    public async Task GetProjectTypeByIdAsync_returns_known_type()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetProjectTypeByIdAsync(VisaryLiveTestIds.ProjectType, default);
        Assert.Equal(VisaryLiveTestIds.ProjectType, dto.ID);
    }

    [SkippableFact]
    public async Task GetInflationCalcMethodByIdAsync_returns_known_method()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetInflationCalcMethodByIdAsync(VisaryLiveTestIds.InflationCalcMethod, default);
        Assert.Equal(VisaryLiveTestIds.InflationCalcMethod, dto.ID);
    }

    [SkippableFact]
    public async Task GetEstateClassByIdAsync_returns_known_class()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetEstateClassByIdAsync(VisaryLiveTestIds.EstateClass, default);
        Assert.Equal(VisaryLiveTestIds.EstateClass, dto.ID);
    }

    [SkippableFact]
    public async Task GetBuildingMaterialByIdAsync_returns_known_material()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetBuildingMaterialByIdAsync(VisaryLiveTestIds.BuildingMaterial, default);
        Assert.Equal(VisaryLiveTestIds.BuildingMaterial, dto.ID);
    }

    [SkippableFact]
    public async Task GetFinishingMaterialByIdAsync_returns_known_material()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetFinishingMaterialByIdAsync(VisaryLiveTestIds.FinishingMaterial, default);
        Assert.Equal(VisaryLiveTestIds.FinishingMaterial, dto.ID);
    }

    [SkippableFact]
    public async Task GetRoomKindByIdAsync_returns_known_kind()
    {
        SkipIfNoToken();
        var dto = await VisaryLiveClientFactory.NewCrud()
            .GetRoomKindByIdAsync(VisaryLiveTestIds.RoomKind, default);
        Assert.Equal(VisaryLiveTestIds.RoomKind, dto.ID);
    }

    private static void SkipIfNoToken()
    {
        var (_, token) = VisaryLiveTestConfig.Resolve();
        Skip.If(string.IsNullOrWhiteSpace(token) || !VisaryLiveTestConfig.IsTokenLikelyAlive(token),
                VisaryLiveTestConfig.SkipReason());
    }
}
