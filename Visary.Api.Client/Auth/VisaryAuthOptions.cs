namespace Visary.Api.Auth;

/// <summary>
/// Конфиг OIDC token-endpoint'а для <see cref="OidcVisaryTokenProvider"/>. Секция
/// <c>Visary:Auth</c> в <c>appsettings.json</c> / env (<c>Visary__Auth__*</c>).
///
/// «Параметры запроса могут добавляться» — нестандартные поля payload'а
/// (resource, acr_values, audience, …) кладутся в
/// <see cref="ExtraTokenRequestParameters"/> без правок кода provider'а.
/// </summary>
public sealed class VisaryAuthOptions
{
    public const string SectionName = "Visary:Auth";

    /// <summary>Полный URL token-endpoint'а, напр. <c>https://id-isup-alfa-test.k8s.npc.ba/oidc/connect/token</c>.</summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>OIDC <c>client_id</c>, напр. <c>visary-ui</c>.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Тип grant'а. По умолчанию <c>refresh_token</c> — единственный реализованный flow
    /// (см. doc 107 — почему не authorization_code/client_credentials).
    /// </summary>
    public string GrantType { get; set; } = "refresh_token";

    /// <summary>Опциональный scope. У нашего IdP не обязателен — refresh-grant отдаёт scope-ы исходной сессии.</summary>
    public string? Scope { get; set; }

    /// <summary>Для confidential client'ов — client_secret. Для public (PKCE, visary-ui) — null.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// За сколько секунд ДО истечения access_token делаем рефреш заранее. Гасит гонку
    /// «токен умер ровно между fast-path-проверкой и SendAsync».
    /// </summary>
    public int RefreshSkewSeconds { get; set; } = 60;

    /// <summary>
    /// Сколько секунд считать токен валидным, если IdP не вернул <c>expires_in</c>
    /// (защита от 0 в кэше → бесконечный рефреш).
    /// </summary>
    public int FallbackLifetimeSeconds { get; set; } = 300;

    /// <summary>
    /// Дополнительные поля POST-payload'а (form-urlencoded). Здесь живут «параметры, которые
    /// могут добавляться» — resource, audience, acr_values и т.п. — без правок provider'а.
    /// </summary>
    public Dictionary<string, string> ExtraTokenRequestParameters { get; set; } = new();
}
