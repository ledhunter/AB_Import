using KiloImportService.Api.Controllers;
using KiloImportService.Api.Visary;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Visary.Api.Dto;

namespace KiloImportService.Api.Tests.Controllers;

/// <summary>
/// Тесты VisaryDictionariesController + VisaryDictionaryRegistry.
/// Гарантия: маршруты разрешаются по urlName, незарегистрированный возвращает 404
/// со списком известных, а handler вызывается с правильными параметрами.
/// </summary>
public sealed class VisaryDictionariesControllerTests
{
    private static VisaryDictionariesController NewController(VisaryDictionaryRegistry registry)
        => new(registry, NullLogger<VisaryDictionariesController>.Instance);

    [Fact]
    public async Task List_unknown_dictionary_returns_404_with_available_names()
    {
        var registry = NewRegistry(("towns", new StubHandler()), ("regions", new StubHandler()));

        var result = await NewController(registry).List("nonexistent", titleFilter: null, default);

        var nf = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(nf.Value);
        // available перечисляет зарегистрированные имена
        var json = System.Text.Json.JsonSerializer.Serialize(nf.Value);
        Assert.Contains("towns", json);
        Assert.Contains("regions", json);
    }

    [Fact]
    public async Task List_routes_to_registered_handler_and_passes_titleFilter()
    {
        var stub = new StubHandler();
        var registry = NewRegistry(("towns", stub));

        var result = await NewController(registry).List("towns", titleFilter: "Mosc", default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Mosc", stub.LastListFilter);
        Assert.Equal(1, stub.ListCalls);
    }

    [Fact]
    public async Task GetById_routes_to_registered_handler_with_id()
    {
        var stub = new StubHandler();
        var registry = NewRegistry(("towns", stub));

        var result = await NewController(registry).GetById("towns", 5565, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(5565, stub.LastGetId);
    }

    [Fact]
    public async Task GetById_for_unknown_dictionary_returns_404()
    {
        var registry = NewRegistry();

        var result = await NewController(registry).GetById("foo", 1, default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var registry = NewRegistry(("towns", new StubHandler()));

        Assert.True(registry.TryGet("TOWNS",  out _));
        Assert.True(registry.TryGet("Towns",  out _));
        Assert.True(registry.TryGet("towns",  out _));
        Assert.False(registry.TryGet("Town",  out _));
    }

    [Fact]
    public void AddVisaryDictionary_extension_registers_handler_in_DI()
    {
        // Проверяем, что extension-метод корректно собирает реестр через DI.
        var services = new ServiceCollection();
        // IListViewClient/ICrudClient в этом тесте не вызываются — handler не дёргается.
        services.AddSingleton(new Mock_StubScopeFactory());
        services.AddSingleton<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(
            sp => sp.GetRequiredService<Mock_StubScopeFactory>());

        services.AddVisaryDictionary<TownRaw>("towns",
            (lv, q, ct) => Task.FromResult(new ListViewResponse<TownRaw>()),
            (cr, id, ct) => Task.FromResult(new TownRaw()));
        services.AddVisaryDictionary<RegionRaw>("regions",
            (lv, q, ct) => Task.FromResult(new ListViewResponse<RegionRaw>()),
            (cr, id, ct) => Task.FromResult(new RegionRaw()));

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<VisaryDictionaryRegistry>();

        Assert.Contains("towns",   registry.RegisteredNames);
        Assert.Contains("regions", registry.RegisteredNames);
    }

    // ─── helpers ───

    private static VisaryDictionaryRegistry NewRegistry(params (string name, IVisaryDictionaryHandler handler)[] items)
    {
        var regs = items.Select(i =>
            (IVisaryDictionaryRegistration)new InlineRegistration(i.name, i.handler));
        return new VisaryDictionaryRegistry(regs);
    }

    private sealed class InlineRegistration : IVisaryDictionaryRegistration
    {
        public InlineRegistration(string name, IVisaryDictionaryHandler handler) { UrlName = name; Handler = handler; }
        public string UrlName { get; }
        public IVisaryDictionaryHandler Handler { get; }
    }

    private sealed class StubHandler : IVisaryDictionaryHandler
    {
        public int     ListCalls       { get; private set; }
        public string? LastListFilter  { get; private set; }
        public int?    LastGetId       { get; private set; }

        public Task<object> ListAsync(string? titleFilter, CancellationToken ct)
        {
            ListCalls++;
            LastListFilter = titleFilter;
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> GetByIdAsync(int id, CancellationToken ct)
        {
            LastGetId = id;
            return Task.FromResult<object>(new { id });
        }
    }

    // Заглушка IServiceScopeFactory, чтобы AddVisaryDictionary мог получить её при разрешении.
    // Сам scope в этом тесте не используется (handler не вызывается).
    private sealed class Mock_StubScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException("scope not used in this test");
    }
}
