using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;

namespace KiloImportService.Api.Visary;

public static class VisaryDictionaryServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует справочник Visary как HTTP-эндпоинт <c>/api/visary/{urlName}</c>.
    /// Делегаты <paramref name="list"/> и <paramref name="getById"/> ссылаются на
    /// type-safe методы из <see cref="IListViewClient"/> и <see cref="ICrudClient"/>.
    /// </summary>
    public static IServiceCollection AddVisaryDictionary<TDto>(
        this IServiceCollection services,
        string urlName,
        Func<IListViewClient, string?, CancellationToken, Task<ListViewResponse<TDto>>> list,
        Func<ICrudClient, int, CancellationToken, Task<TDto>> getById)
    {
        services.TryAddSingleton<VisaryDictionaryRegistry>();
        services.AddSingleton<IVisaryDictionaryRegistration>(sp =>
            new VisaryDictionaryRegistration<TDto>(
                urlName,
                sp.GetRequiredService<IServiceScopeFactory>(),
                list,
                getById));
        return services;
    }
}
