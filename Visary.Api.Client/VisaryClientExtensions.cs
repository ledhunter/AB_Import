using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Visary.Api.Auth;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.FileStorage;
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
        // Auth-pipeline: handler сам разрешает IVisaryTokenProvider из DI и подставляет Bearer.
        services.AddTransient<VisaryAuthHandler>();

        services.AddHttpClient<IListViewClient, ListViewClient>(ConfigureHttpClient)
                .AddHttpMessageHandler<VisaryAuthHandler>();
        services.AddHttpClient<ICrudClient, CrudClient>(ConfigureHttpClient)
                .AddHttpMessageHandler<VisaryAuthHandler>();
        services.AddHttpClient<IFileStorageClient, FileStorageClient>(ConfigureHttpClient)
                .AddHttpMessageHandler<VisaryAuthHandler>();

        // Дефолтный provider — StaticVisaryTokenProvider (читает VisaryOptions.BearerToken).
        // Замещается на OidcVisaryTokenProvider через AddVisaryOidcAuth — см. doc 107.
        services.TryAddSingleton<IVisaryTokenProvider, StaticVisaryTokenProvider>();
        return services;
    }

    /// <summary>
    /// Подключает OIDC refresh_token-flow для запросов в Visary. Замещает дефолтный
    /// <see cref="StaticVisaryTokenProvider"/> на <see cref="OidcVisaryTokenProvider"/>.
    ///
    /// Refresh-token store по умолчанию — <see cref="EnvironmentRefreshTokenStore"/>
    /// (читает env <c>VISARY_AUTH_REFRESH_TOKEN</c>). Для prod-окружения замените на
    /// <see cref="VaultRefreshTokenStore"/>:
    /// <code>services.Replace(ServiceDescriptor.Singleton&lt;IVisaryRefreshTokenStore, VaultRefreshTokenStore&gt;())</code>.
    ///
    /// Вызывать ПОСЛЕ <see cref="AddVisaryClient(IServiceCollection, IConfiguration)"/>.
    /// </summary>
    public static IServiceCollection AddVisaryOidcAuth(
        this IServiceCollection services,
        IConfiguration authSection)
    {
        services.Configure<VisaryAuthOptions>(authSection);
        return RegisterOidcAuth(services);
    }

    public static IServiceCollection AddVisaryOidcAuth(
        this IServiceCollection services,
        Action<VisaryAuthOptions> configureAuth)
    {
        services.Configure(configureAuth);
        return RegisterOidcAuth(services);
    }

    private static IServiceCollection RegisterOidcAuth(IServiceCollection services)
    {
        // Отдельный HttpClient для запросов к IdP — БЕЗ VisaryAuthHandler в pipeline'е,
        // иначе при первом рефреше будет рекурсия (для запроса токена нужен токен).
        services.AddHttpClient(OidcVisaryTokenProvider.TokenHttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        // Singleton — кэш токена должен жить дольше scope'а запроса; иначе каждый
        // scoped-сервис будет делать свой рефреш.
        services.Replace(ServiceDescriptor.Singleton<IVisaryTokenProvider, OidcVisaryTokenProvider>());

        // Дефолтный store — env-var. На prod замещается явным services.Replace(...).
        services.TryAddSingleton<IVisaryRefreshTokenStore>(_ => new EnvironmentRefreshTokenStore());
        return services;
    }

    private static void ConfigureHttpClient(IServiceProvider sp, HttpClient client)
    {
        var opt = sp.GetRequiredService<IOptionsMonitor<VisaryOptions>>().CurrentValue;
        if (opt.RequestTimeout > TimeSpan.Zero)
            client.Timeout = opt.RequestTimeout;
    }
}
