using System.Text.Json;

namespace KiloImportService.Api.Tests.VisaryClients;

/// <summary>
/// Контракт-тесты ListViewClient: фиксируют HTTP-метод, URL и JSON-тело каждого запроса.
/// Также защищают от регрессии в helper'ах фильтров (FilterByString и т.п.) — экранирование
/// должно остаться корректным, иначе откроется JSON-инъекция.
/// </summary>
public sealed class ListViewClientContractTests
{
    private const string Base = TestVisaryClientFactory.BaseUrl;

    private const string EmptyListResponse =
        "{\"Total\":0,\"Data\":[],\"Summary\":null}";

    [Fact]
    public async Task GetProjectsAsync_posts_to_listview_constructionproject()
    {
        var (client, handler) = TestVisaryClientFactory.NewListView();
        handler.EnqueueJson(EmptyListResponse);

        await client.GetProjectsAsync(search: "alpha", pageSize: 10, ct: default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"{Base}/api/visary/listview/constructionproject", req.RequestUri!.ToString());
        var body = handler.RequestBodies[0]!;
        Assert.Contains("\"Mnemonic\":\"constructionproject\"", body);
        Assert.Contains("\"PageSize\":10", body);
        Assert.Contains("\"SearchString\":\"alpha\"", body);
    }

    [Fact]
    public async Task GetSitesByProjectAsync_uses_onetomany_route_with_associationId()
    {
        var (client, handler) = TestVisaryClientFactory.NewListView();
        handler.EnqueueJson(EmptyListResponse);

        await client.GetSitesByProjectAsync(4584, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(
            $"{Base}/api/visary/listview/constructionsite/onetomany/Project?associationId=4584",
            req.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetSiteByProjectAndIdAsync_filters_already_loaded_list_by_id_in_memory()
    {
        var (client, handler) = TestVisaryClientFactory.NewListView();
        handler.EnqueueJson(
            "{\"Total\":2,\"Data\":[{\"ID\":1},{\"ID\":99}]}");

        var found = await client.GetSiteByProjectAndIdAsync(4584, 99, default);

        Assert.NotNull(found);
        Assert.Equal(99, found!.ID);
        Assert.Single(handler.Requests); // один сетевой вызов, фильтрация на клиенте
    }

    [Theory]
    [InlineData("LmID", "L-1234", "[\"LmID\",\"=\",\"L-1234\"]")]
    [InlineData("Title", "Дом № 5", "[\"Title\",\"=\",\"\\u0414\\u043E\\u043C \\u2116 5\"]")]
    [InlineData("Title", "with \"quote\"", "[\"Title\",\"=\",\"with \\u0022quote\\u0022\"]")]
    public async Task FilterByString_escapes_value_safely(string field, string value, string expectedJson)
    {
        var (client, handler) = TestVisaryClientFactory.NewListView();
        handler.EnqueueJson(EmptyListResponse);

        // Дёргаем любой публичный метод, который проксирует фильтр через FilterByString:
        // GetDealsAsync(lmIdFilter) кладёт filter в body как строку JSON.
        if (field == "LmID")
            await client.GetDealsAsync(value, default);
        else
            await client.GetIndicatorsBySiteAsync(siteId: 1, titleFilter: value, ct: default);

        var body = handler.RequestBodies[0]!;
        // Значение фильтра внутри тела JSON-сериализовано ещё раз (filter — это строка).
        // Проверяем, что наш JsonSerializer корректно экранировал ", \\, и Unicode.
        var doc = JsonDocument.Parse(body);
        var filter = doc.RootElement.GetProperty("Filter").GetString();
        Assert.Equal(expectedJson, filter);
    }

    [Fact]
    public async Task ListTownsAsync_uses_dictionary_columns_and_LargePageSize()
    {
        var (client, handler) = TestVisaryClientFactory.NewListView();
        handler.EnqueueJson(EmptyListResponse);

        await client.ListTownsAsync("Mosc", default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal($"{Base}/api/visary/listview/town", req.RequestUri!.ToString());
        var body = handler.RequestBodies[0]!;
        Assert.Contains("\"Mnemonic\":\"town\"", body);
        Assert.Contains("\"PageSize\":500", body); // LargePageSize дефолт
        Assert.Contains("\"Filter\":\"[\\u0022Title\\u0022,\\u0022=\\u0022,\\u0022Mosc\\u0022]\"", body);
    }

    [Theory]
    // Каждая мнемоника-справочник должна попасть в свой listview-эндпоинт.
    [InlineData("town")]
    [InlineData("region")]
    [InlineData("projecttype")]
    [InlineData("inflationcalcmethod")]
    [InlineData("estateclass")]
    [InlineData("buildingmaterial")]
    [InlineData("finishingmaterial")]
    [InlineData("roomkind")]
    public async Task Each_dictionary_list_method_hits_correct_url(string mnemonic)
    {
        var (client, handler) = TestVisaryClientFactory.NewListView();
        handler.EnqueueJson(EmptyListResponse);

        Task task = mnemonic switch
        {
            "town"                => client.ListTownsAsync(null, default),
            "region"              => client.ListRegionsAsync(null, default),
            "projecttype"         => client.ListProjectTypesAsync(default),
            "inflationcalcmethod" => client.ListInflationCalcMethodsAsync(default),
            "estateclass"         => client.ListEstateClassesAsync(default),
            "buildingmaterial"    => client.ListBuildingMaterialsAsync(default),
            "finishingmaterial"   => client.ListFinishingMaterialsAsync(default),
            "roomkind"            => client.ListRoomKindsAsync(default),
            _ => throw new ArgumentOutOfRangeException(nameof(mnemonic)),
        };
        await task;

        var req = Assert.Single(handler.Requests);
        Assert.Equal($"{Base}/api/visary/listview/{mnemonic}", req.RequestUri!.ToString());
    }
}
