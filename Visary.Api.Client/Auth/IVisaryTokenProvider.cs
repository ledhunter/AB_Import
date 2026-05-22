namespace Visary.Api.Auth;

/// <summary>
/// Источник свежего access_token для запросов в Visary. Единственная точка, на которую
/// смотрит <see cref="VisaryAuthHandler"/>; конкретная стратегия (static/OIDC) меняется
/// через DI без правок client'ов.
///
/// Реализации:
///   • <see cref="StaticVisaryTokenProvider"/> — токен из <c>VisaryOptions.BearerToken</c>
///     (dev/CI fallback, не умеет refresh).
///   • <see cref="OidcVisaryTokenProvider"/> — refresh_token grant против IdP, кэширует
///     access_token до <c>exp − RefreshSkewSeconds</c>, ротирует refresh_token через
///     <see cref="IVisaryRefreshTokenStore"/>.
/// </summary>
public interface IVisaryTokenProvider
{
    /// <summary>
    /// Возвращает валидный access_token. Гарантирует, что вернётся НЕ-просроченный токен:
    /// при необходимости делает рефреш под single-flight-блокировкой.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Сбрасывает кэшированный токен — следующий <see cref="GetAccessTokenAsync"/> сделает
    /// принудительный рефреш. Вызывается <see cref="VisaryAuthHandler"/> при 401
    /// (защита от гонки «токен истёк ровно во время запроса»).
    /// </summary>
    Task InvalidateAsync(CancellationToken ct = default);
}
