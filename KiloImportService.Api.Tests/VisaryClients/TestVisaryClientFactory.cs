using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Visary.Api.Auth;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;

namespace KiloImportService.Api.Tests.VisaryClients;

/// <summary>Сборщик клиентов Visary, поверх <see cref="RecordingHttpHandler"/>.</summary>
internal static class TestVisaryClientFactory
{
    public const string BaseUrl = "https://stub.visary.test";
    public const string Token   = "stub-token";

    public static (ListViewClient Client, RecordingHttpHandler Handler) NewListView()
    {
        var handler = new RecordingHttpHandler();
        var http    = NewClientWithAuthPipeline(handler, Token);
        var opt     = new VisaryOptions
        {
            Endpoint    = BaseUrl,
            BearerToken = Token,
            DefaultPageSize = 50,
            LargePageSize   = 500,
        };
        var monitor = new TestOptionsMonitor<VisaryOptions>(opt);
        return (new ListViewClient(http, monitor, NullLogger<ListViewClient>.Instance), handler);
    }

    public static (CrudClient Client, RecordingHttpHandler Handler) NewCrud()
    {
        var handler = new RecordingHttpHandler();
        var http    = NewClientWithAuthPipeline(handler, Token);
        var opt     = new VisaryOptions { Endpoint = BaseUrl, BearerToken = Token };
        var monitor = new TestOptionsMonitor<VisaryOptions>(opt);
        return (new CrudClient(http, monitor, NullLogger<CrudClient>.Instance), handler);
    }

    // Обёртка: VisaryAuthHandler сидит над RecordingHttpHandler — тем же путём, что в продовом
    // pipeline'е через IHttpClientFactory. Это позволяет контракт-тестам проверять
    // Authorization-заголовок, при том что Authorization выставляет именно handler, а не клиент.
    internal static HttpClient NewClientWithAuthPipeline(HttpMessageHandler inner, string token)
    {
        var tokenProvider = new StubVisaryTokenProvider(token);
        var auth = new VisaryAuthHandler(tokenProvider, NullLogger<VisaryAuthHandler>.Instance)
        {
            InnerHandler = inner,
        };
        return new HttpClient(auth) { BaseAddress = null };
    }
}

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) { CurrentValue = value; }
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Тестовый <see cref="IVisaryTokenProvider"/>, возвращающий заранее заданный токен.</summary>
internal sealed class StubVisaryTokenProvider : IVisaryTokenProvider
{
    private readonly string _token;
    public int InvalidateCalls { get; private set; }
    public StubVisaryTokenProvider(string token) { _token = token; }
    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(_token);
    public Task InvalidateAsync(CancellationToken ct = default) { InvalidateCalls++; return Task.CompletedTask; }
}
