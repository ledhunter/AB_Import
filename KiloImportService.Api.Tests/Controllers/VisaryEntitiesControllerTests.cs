using KiloImportService.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;

namespace KiloImportService.Api.Tests.Controllers;

/// <summary>
/// Тесты VisaryEntitiesController: убеждаемся, что каждый action пробрасывает запрос
/// в нужный метод IListViewClient/ICrudClient. Если кто-то перепутает метод (например,
/// в ListProjects вызовет GetSitesByProjectAsync) — тест упадёт.
/// </summary>
public sealed class VisaryEntitiesControllerTests
{
    private static (VisaryEntitiesController Controller, Mock<IListViewClient> Lv, Mock<ICrudClient> Cr) NewController()
    {
        var lv = new Mock<IListViewClient>(MockBehavior.Strict);
        var cr = new Mock<ICrudClient>(MockBehavior.Strict);
        return (new VisaryEntitiesController(lv.Object, cr.Object), lv, cr);
    }

    private static ListViewResponse<T> EmptyList<T>() => new() { Total = 0, Data = new List<T>() };

    [Fact]
    public async Task ListProjects_calls_GetProjectsAsync_with_query_params()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetProjectsAsync("alpha", 7, default))
          .ReturnsAsync(EmptyList<ConstructionProjectRaw>());

        var result = await c.ListProjects("alpha", 7, default);

        lv.VerifyAll();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetProject_calls_GetProjectByIdFullAsync()
    {
        var (c, _, cr) = NewController();
        cr.Setup(x => x.GetProjectByIdFullAsync(4584, default))
          .ReturnsAsync(new ConstructionProjectFull { ID = 4584 });

        var result = await c.GetProject(4584, default);

        cr.VerifyAll();
        var full = Assert.IsType<ConstructionProjectFull>(result);
        Assert.Equal(4584, full.ID);
    }

    [Fact]
    public async Task ListSites_calls_GetSitesByProjectAsync()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetSitesByProjectAsync(4584, default))
          .ReturnsAsync(EmptyList<ConstructionSiteRaw>());

        await c.ListSites(4584, default);
        lv.VerifyAll();
    }

    [Fact]
    public async Task GetSite_uses_ICrudClient_for_full_DTO()
    {
        var (c, _, cr) = NewController();
        cr.Setup(x => x.GetSiteByIdFullAsync(7850, default))
          .ReturnsAsync(new ConstructionSiteFull { ID = 7850 });

        var result = await c.GetSite(7850, default);
        Assert.IsType<ConstructionSiteFull>(result);
    }

    [Fact]
    public async Task ListRooms_with_siteId_calls_GetRoomsBySiteAsync()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetRoomsBySiteAsync(7850, "u-1", default))
          .ReturnsAsync(EmptyList<RoomRaw>());

        var result = await c.ListRooms(siteId: 7850, sectionId: null, uniqueNumberFilter: "u-1", default);

        lv.VerifyAll();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ListRooms_with_sectionId_calls_GetRoomsBySectionAsync()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetRoomsBySectionAsync(615, null, default))
          .ReturnsAsync(EmptyList<RoomRaw>());

        var result = await c.ListRooms(siteId: null, sectionId: 615, uniqueNumberFilter: null, default);

        lv.VerifyAll();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ListRooms_without_filters_returns_BadRequest()
    {
        var (c, _, _) = NewController();

        var result = await c.ListRooms(siteId: null, sectionId: null, uniqueNumberFilter: null, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ListDeals_with_projectId_calls_GetDealsByProjectAsync()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetDealsByProjectAsync(4584, "lm-1", null, default))
          .ReturnsAsync(EmptyList<DealRaw>());

        await c.ListDeals(projectId: 4584, lmIdFilter: "lm-1", docNumberFilter: null, default);
        lv.VerifyAll();
    }

    [Fact]
    public async Task ListDeals_with_projectId_and_docNumber_passes_both_filters()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetDealsByProjectAsync(4584, "lm-1", "DN-7", default))
          .ReturnsAsync(EmptyList<DealRaw>());

        await c.ListDeals(projectId: 4584, lmIdFilter: "lm-1", docNumberFilter: "DN-7", default);
        lv.VerifyAll();
    }

    [Fact]
    public async Task ListDeals_without_projectId_calls_GetDealsAsync()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetDealsAsync(null, null, default))
          .ReturnsAsync(EmptyList<DealRaw>());

        await c.ListDeals(projectId: null, lmIdFilter: null, docNumberFilter: null, default);
        lv.VerifyAll();
    }

    [Fact]
    public async Task ListDeals_without_projectId_passes_both_filters()
    {
        // doc 104 v1.2: глобальный listview/deal должен получать оба фильтра, если они
        // заданы в query, — фронт может звать proxy и для нового FinModel-fallback-кейса.
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetDealsAsync("L-1", "DN-7", default))
          .ReturnsAsync(EmptyList<DealRaw>());

        await c.ListDeals(projectId: null, lmIdFilter: "L-1", docNumberFilter: "DN-7", default);
        lv.VerifyAll();
    }

    [Fact]
    public async Task GetOrganization_calls_GetOrganizationByIdAsync()
    {
        var (c, _, cr) = NewController();
        cr.Setup(x => x.GetOrganizationByIdAsync(4499, default))
          .ReturnsAsync(new OrganizationFull { ID = 4499 });

        await c.GetOrganization(4499, default);
        cr.VerifyAll();
    }

    [Fact]
    public async Task ListOrganizations_calls_GetOrganizationsByClientIdAsync()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.GetOrganizationsByClientIdAsync("2", default))
          .ReturnsAsync(EmptyList<OrganizationRaw>());

        await c.ListOrganizations("2", default);
        lv.VerifyAll();
    }

    [Fact]
    public async Task ListCadastralAreas_calls_ListCadastralAreasAsync()
    {
        var (c, lv, _) = NewController();
        lv.Setup(x => x.ListCadastralAreasAsync("77:01", default))
          .ReturnsAsync(EmptyList<CadastralAreaFull>());

        await c.ListCadastralAreas("77:01", default);
        lv.VerifyAll();
    }
}
