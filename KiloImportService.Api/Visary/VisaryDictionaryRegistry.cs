using Microsoft.Extensions.DependencyInjection;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;

namespace KiloImportService.Api.Visary;

/// <summary>
/// Реестр справочников Visary, проксируемых наружу как HTTP-эндпоинты
/// <c>GET /api/visary/{name}</c> и <c>GET /api/visary/{name}/{id}</c>.
///
/// Расширение: чтобы добавить новый справочник — одна строка в Program.cs:
/// <code>
/// builder.Services.AddVisaryDictionary&lt;CurrencyRaw&gt;(
///     "currencies",
///     list:    (lv, q, ct) =&gt; lv.ListCurrenciesAsync(q, ct),
///     getById: (cr, id, ct) =&gt; cr.GetCurrencyByIdAsync(id, ct));
/// </code>
/// Никаких изменений в контроллере при этом не требуется.
/// </summary>
public sealed class VisaryDictionaryRegistry
{
    private readonly Dictionary<string, IVisaryDictionaryHandler> _handlers;

    public VisaryDictionaryRegistry(IEnumerable<IVisaryDictionaryRegistration> registrations)
    {
        _handlers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in registrations)
        {
            if (_handlers.ContainsKey(r.UrlName))
                throw new InvalidOperationException($"Справочник '{r.UrlName}' зарегистрирован дважды.");
            _handlers[r.UrlName] = r.Handler;
        }
    }

    public bool TryGet(string urlName, out IVisaryDictionaryHandler handler)
        => _handlers.TryGetValue(urlName, out handler!);

    public IReadOnlyCollection<string> RegisteredNames => _handlers.Keys;
}

/// <summary>Декларативная запись о справочнике (имя URL + handler).
/// Регистрируется в DI; <see cref="VisaryDictionaryRegistry"/> собирает все при создании.</summary>
public interface IVisaryDictionaryRegistration
{
    string UrlName { get; }
    IVisaryDictionaryHandler Handler { get; }
}

/// <summary>Контракт одной записи реестра. Возвращает <see cref="object"/>, потому что
/// конкретный тип DTO известен только в обобщённой реализации.</summary>
public interface IVisaryDictionaryHandler
{
    Task<object> ListAsync(string? titleFilter, CancellationToken ct);
    Task<object> GetByIdAsync(int id, CancellationToken ct);
}

/// <summary>
/// Универсальный handler. Параметризуется DTO-типом и двумя делегатами, которые
/// вызывают type-safe методы клиентов из Visary.Api.Client. Использует
/// <see cref="IServiceScopeFactory"/>, чтобы каждый запрос получал свежий scope для
/// scoped HTTP-клиентов (singleton-handler не должен держать scoped-зависимости).
/// </summary>
public sealed class VisaryDictionaryHandler<TDto> : IVisaryDictionaryHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Func<IListViewClient, string?, CancellationToken, Task<ListViewResponse<TDto>>> _list;
    private readonly Func<ICrudClient, int, CancellationToken, Task<TDto>> _getById;

    public VisaryDictionaryHandler(
        IServiceScopeFactory scopes,
        Func<IListViewClient, string?, CancellationToken, Task<ListViewResponse<TDto>>> list,
        Func<ICrudClient, int, CancellationToken, Task<TDto>> getById)
    {
        _scopes  = scopes;
        _list    = list;
        _getById = getById;
    }

    public async Task<object> ListAsync(string? titleFilter, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var lv = scope.ServiceProvider.GetRequiredService<IListViewClient>();
        return await _list(lv, titleFilter, ct);
    }

    public async Task<object> GetByIdAsync(int id, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var cr = scope.ServiceProvider.GetRequiredService<ICrudClient>();
        return await _getById(cr, id, ct);
    }
}

internal sealed class VisaryDictionaryRegistration<TDto> : IVisaryDictionaryRegistration
{
    public string UrlName { get; }
    public IVisaryDictionaryHandler Handler { get; }

    public VisaryDictionaryRegistration(
        string urlName,
        IServiceScopeFactory scopes,
        Func<IListViewClient, string?, CancellationToken, Task<ListViewResponse<TDto>>> list,
        Func<ICrudClient, int, CancellationToken, Task<TDto>> getById)
    {
        UrlName = urlName;
        Handler = new VisaryDictionaryHandler<TDto>(scopes, list, getById);
    }
}
