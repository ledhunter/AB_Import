using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;
using KiloImportService.Api.Tests.VisaryClients;

namespace KiloImportService.Api.Tests.VisaryLive;

/// <summary>Собирает реальные CrudClient/ListViewClient, бьющие в живой Visary.</summary>
internal static class VisaryLiveClientFactory
{
    public static CrudClient NewCrud()
    {
        var (baseUrl, token) = VisaryLiveTestConfig.Resolve();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var monitor = new TestOptionsMonitor<VisaryOptions>(new VisaryOptions
        {
            BaseUrl     = baseUrl!,
            BearerToken = token!,
            DefaultPageSize = 50,
            LargePageSize   = 500,
        });
        return new CrudClient(http, monitor, NullLogger<CrudClient>.Instance);
    }

    public static ListViewClient NewListView()
    {
        var (baseUrl, token) = VisaryLiveTestConfig.Resolve();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var monitor = new TestOptionsMonitor<VisaryOptions>(new VisaryOptions
        {
            BaseUrl     = baseUrl!,
            BearerToken = token!,
            DefaultPageSize = 50,
            LargePageSize   = 500,
        });
        return new ListViewClient(http, monitor, NullLogger<ListViewClient>.Instance);
    }
}
