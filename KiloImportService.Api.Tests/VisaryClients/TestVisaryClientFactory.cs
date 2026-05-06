using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        var http = new HttpClient(handler) { BaseAddress = null };
        var opt  = new VisaryOptions
        {
            BaseUrl     = BaseUrl,
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
        var http = new HttpClient(handler) { BaseAddress = null };
        var opt  = new VisaryOptions { BaseUrl = BaseUrl, BearerToken = Token };
        var monitor = new TestOptionsMonitor<VisaryOptions>(opt);
        return (new CrudClient(http, monitor, NullLogger<CrudClient>.Instance), handler);
    }
}

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) { CurrentValue = value; }
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
