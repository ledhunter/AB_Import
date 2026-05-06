using System.Net.Http.Headers;

namespace KiloImportService.Api.Tests.VisaryClients;

/// <summary>
/// Контракт-тесты CrudClient: фиксируют HTTP-метод, URL и заголовок Authorization для
/// каждого публичного метода. Если кто-то изменит URL-шаблон или забудет токен — тест упадёт.
/// </summary>
public sealed class CrudClientContractTests
{
    private const string Base = TestVisaryClientFactory.BaseUrl;

    [Fact]
    public async Task GetByIdAsync_generic_uses_crud_url_and_bearer_header()
    {
        var (client, handler) = TestVisaryClientFactory.NewCrud();
        handler.EnqueueJson("{\"ID\":42}");

        await client.GetByIdAsync<TestDto>("foobar", 42, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal($"{Base}/api/visary/crud/foobar/42", req.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", TestVisaryClientFactory.Token).ToString(),
                     req.Headers.Authorization!.ToString());
    }

    public sealed class TestDto { public int ID { get; set; } }

    [Theory]
    [InlineData("constructionproject")]
    [InlineData("constructionsite")]
    [InlineData("constructionsection")]
    [InlineData("constructionsiteindicator")]
    [InlineData("constructionsiteindicatorvalue")]
    [InlineData("room")]
    [InlineData("cadastralarea")]
    [InlineData("percentbet")]
    [InlineData("shareagreement")]
    [InlineData("deal")]
    [InlineData("organization")]
    [InlineData("town")]
    [InlineData("region")]
    [InlineData("projecttype")]
    [InlineData("inflationcalcmethod")]
    [InlineData("estateclass")]
    [InlineData("buildingmaterial")]
    [InlineData("finishingmaterial")]
    [InlineData("roomkind")]
    public async Task Each_typed_GetByIdAsync_hits_correct_crud_url(string mnemonic)
    {
        var (client, handler) = TestVisaryClientFactory.NewCrud();
        handler.EnqueueJson("{\"ID\":7}");

        // Резолвим typed-метод по соответствию мнемонике, чтобы тест явно покрыл каждую сущность.
        Task task = mnemonic switch
        {
            "constructionproject"            => client.GetProjectByIdFullAsync(7, default),
            "constructionsite"               => client.GetSiteByIdFullAsync(7, default),
            "constructionsection"            => client.GetSectionByIdAsync(7, default),
            "constructionsiteindicator"      => client.GetIndicatorByIdAsync(7, default),
            "constructionsiteindicatorvalue" => client.GetIndicatorValueByIdAsync(7, default),
            "room"                           => client.GetRoomByIdAsync(7, default),
            "cadastralarea"                  => client.GetCadastralAreaByIdAsync(7, default),
            "percentbet"                     => client.GetPercentBetByIdAsync(7, default),
            "shareagreement"                 => client.GetShareAgreementByIdAsync(7, default),
            "deal"                           => client.GetDealByIdAsync(7, default),
            "organization"                   => client.GetOrganizationByIdAsync(7, default),
            "town"                           => client.GetTownByIdAsync(7, default),
            "region"                         => client.GetRegionByIdAsync(7, default),
            "projecttype"                    => client.GetProjectTypeByIdAsync(7, default),
            "inflationcalcmethod"            => client.GetInflationCalcMethodByIdAsync(7, default),
            "estateclass"                    => client.GetEstateClassByIdAsync(7, default),
            "buildingmaterial"               => client.GetBuildingMaterialByIdAsync(7, default),
            "finishingmaterial"              => client.GetFinishingMaterialByIdAsync(7, default),
            "roomkind"                       => client.GetRoomKindByIdAsync(7, default),
            _ => throw new ArgumentOutOfRangeException(nameof(mnemonic)),
        };
        await task;

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal($"{Base}/api/visary/crud/{mnemonic}/7", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task PatchSiteAsync_sends_PATCH_with_id_in_route_and_body()
    {
        var (client, handler) = TestVisaryClientFactory.NewCrud();
        handler.EnqueueJson("{}");

        await client.PatchSiteAsync(123, new global::Visary.Api.Dto.SitePatchRequest { RowVersion = 42 }, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal($"{Base}/api/visary/crud/constructionsite/123?forceUpdate=false",
                     req.RequestUri!.ToString());
        Assert.Contains("\"ID\":123", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task PatchSiteAsync_throws_when_request_id_conflicts_with_route_id()
    {
        var (client, _) = TestVisaryClientFactory.NewCrud();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PatchSiteAsync(123, new global::Visary.Api.Dto.SitePatchRequest { ID = 999 }, default));
    }
}
