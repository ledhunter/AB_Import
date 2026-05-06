using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;

namespace Visary.Api;

public static class VisaryClientExtensions
{
    public static IServiceCollection AddVisaryClient(
        this IServiceCollection services,
        IConfiguration configurationSection)
    {
        services.Configure<VisaryOptions>(configurationSection);
        return RegisterClients(services);
    }

    public static IServiceCollection AddVisaryClient(
        this IServiceCollection services,
        Action<VisaryOptions> configureOptions)
    {
        services.Configure(configureOptions);
        return RegisterClients(services);
    }

    private static IServiceCollection RegisterClients(IServiceCollection services)
    {
        services.AddHttpClient<IListViewClient, ListViewClient>(ConfigureHttpClient);
        services.AddHttpClient<ICrudClient, CrudClient>(ConfigureHttpClient);
        return services;
    }

    private static void ConfigureHttpClient(IServiceProvider sp, HttpClient client)
    {
        var opt = sp.GetRequiredService<IOptionsMonitor<VisaryOptions>>().CurrentValue;
        if (opt.RequestTimeout > TimeSpan.Zero)
            client.Timeout = opt.RequestTimeout;
    }
}
