using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Visary.Api.CRUD;
using Visary.Api.Exceptions;
using Visary.Api.ListView;
using Visary.Api.Dto;

namespace Visary.Api;

public static class VisaryClientExtensions
{
    public static IServiceCollection AddVisaryClient(
        this IServiceCollection services,
        Action<VisaryOptions> configureOptions)
    {
        services.Configure(configureOptions);

        services.AddHttpClient<IListViewClient, ListViewClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<VisaryOptions>>().Value;
            if (opt.RequestTimeout > TimeSpan.Zero)
                client.Timeout = opt.RequestTimeout;
        });

        services.AddHttpClient<ICrudClient, CrudClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<VisaryOptions>>().Value;
            if (opt.RequestTimeout > TimeSpan.Zero)
                client.Timeout = opt.RequestTimeout;
        });

        return services;
    }
}
