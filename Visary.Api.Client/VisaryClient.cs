using Visary.Api.Dto;
using Visary.Api.CRUD;
using Visary.Api.Exceptions;
using Visary.Api.ListView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Visary.Api;

public static class VisaryClientExtensions
{
    public static IServiceCollection AddVisaryClient(this IServiceCollection services, Action<VisaryOptions> configureOptions)
    {
        services.AddOptions<VisaryOptions>()
            .Configure(configureOptions);

        services.AddScoped<IListViewClient, ListViewClient>();
        services.AddScoped<ICrudClient, CrudClient>();
        services.AddScoped<IVisaryClient, VisaryClient>();

        return services;
    }
}

public sealed class VisaryClient : IVisaryClient, IDisposable
{
    private readonly IListViewClient _listViewClient;
    private readonly ICrudClient _crudClient;

    public VisaryClient(
        IListViewClient listViewClient,
        ICrudClient crudClient,
        IOptions<VisaryOptions> options)
    {
        _listViewClient = listViewClient;
        _crudClient = crudClient;
        Options = options.Value;
    }

    public IListViewClient ListView => _listViewClient;
    public ICrudClient Crud => _crudClient;
    public VisaryOptions Options { get; }

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _listViewClient.GetProjectsAsync(null, 1, ct);
    }

    public void Dispose()
    {
        (_listViewClient as IDisposable)?.Dispose();
        (_crudClient as IDisposable)?.Dispose();
    }
}
