using Microsoft.Extensions.Options;
using Visary.Api.Dto;

namespace Visary.Api.Auth;

/// <summary>
/// Bridge для текущего поведения — возвращает <see cref="VisaryOptions.BearerToken"/>
/// из <c>appsettings</c>/<c>.env</c>. Используется как dev/CI fallback и в контракт-тестах,
/// где живой OIDC не нужен. Если <c>BearerToken</c> пустой — бросаем явно, чтобы ошибка
/// не маскировалась 401-м от Visary.
/// </summary>
public sealed class StaticVisaryTokenProvider : IVisaryTokenProvider
{
    private readonly IOptionsMonitor<VisaryOptions> _options;

    public StaticVisaryTokenProvider(IOptionsMonitor<VisaryOptions> options)
    {
        _options = options;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var token = _options.CurrentValue.BearerToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "StaticVisaryTokenProvider: EndpointsConfiguration:VisaryApi:BearerToken не задан. " +
                "Заполните .env (EndpointsConfiguration__VisaryApi__BearerToken) либо включите " +
                "OidcVisaryTokenProvider (см. doc 107 + doc 145).");
        return Task.FromResult(token);
    }

    public Task InvalidateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
