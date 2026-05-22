using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Visary.Api.Exceptions;

namespace Visary.Api.Auth;

/// <summary>
/// OIDC refresh_token-flow: POST <c>{TokenEndpoint}</c> с <c>grant_type=refresh_token</c>,
/// кэширует access_token до <c>exp − RefreshSkewSeconds</c>, ротирует refresh_token
/// (IdP может вернуть новый — пишем обратно через <see cref="IVisaryRefreshTokenStore"/>).
///
/// Single-flight: 100 параллельных room-rows под Apply'ем не порождают 100 запросов в IdP —
/// fast-path без блокировки; рефреш под <see cref="SemaphoreSlim"/>; double-check внутри замка.
///
/// HttpClient берётся через <see cref="IHttpClientFactory"/> по имени
/// <see cref="TokenHttpClientName"/> — это отдельный канал БЕЗ <see cref="VisaryAuthHandler"/>,
/// иначе при рефреше у нас была бы рекурсия (для запроса токена нужен токен).
/// </summary>
public sealed class OidcVisaryTokenProvider : IVisaryTokenProvider, IDisposable
{
    /// <summary>
    /// Имя HttpClient'а для запросов к IdP — должен быть зарегистрирован отдельно,
    /// без <see cref="VisaryAuthHandler"/> в pipeline'е.
    /// </summary>
    public const string TokenHttpClientName = "VisaryOidcTokenClient";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<VisaryAuthOptions> _options;
    private readonly IVisaryRefreshTokenStore _refreshStore;
    private readonly ILogger<OidcVisaryTokenProvider> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt = DateTimeOffset.MinValue;

    public OidcVisaryTokenProvider(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<VisaryAuthOptions> options,
        IVisaryRefreshTokenStore refreshStore,
        ILogger<OidcVisaryTokenProvider> log)
    {
        _httpFactory  = httpFactory;
        _options      = options;
        _refreshStore = refreshStore;
        _log          = log;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var opt  = _options.CurrentValue;
        var skew = TimeSpan.FromSeconds(Math.Max(0, opt.RefreshSkewSeconds));

        // Fast-path: токен жив с запасом — без блокировки.
        if (_accessToken is not null && DateTimeOffset.UtcNow + skew < _accessExpiresAt)
            return _accessToken;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check: возможно, пока ждали семафор, кто-то уже обновил.
            if (_accessToken is not null && DateTimeOffset.UtcNow + skew < _accessExpiresAt)
                return _accessToken;

            await RefreshLockedAsync(opt, ct).ConfigureAwait(false);
            return _accessToken
                ?? throw new VisaryAuthException("OIDC refresh не вернул access_token.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _accessToken     = null;
            _accessExpiresAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshLockedAsync(VisaryAuthOptions opt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.TokenEndpoint))
            throw new InvalidOperationException("Visary:Auth:TokenEndpoint не задан.");
        if (string.IsNullOrWhiteSpace(opt.ClientId))
            throw new InvalidOperationException("Visary:Auth:ClientId не задан.");

        var refreshToken = await _refreshStore.GetAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                "OidcVisaryTokenProvider: refresh_token пуст. Один раз залогиньтесь в Visary UI " +
                "с scope=offline_access, сохраните refresh_token в Vault (или env " +
                $"{EnvironmentRefreshTokenStore.DefaultEnvVarName} для dev) — см. doc_project/107.");

        // form-urlencoded payload. ExtraTokenRequestParameters позволяет добавить любые
        // нестандартные поля (resource, audience, acr_values) без правок provider'а.
        var payload = new Dictionary<string, string>
        {
            ["grant_type"]    = string.IsNullOrWhiteSpace(opt.GrantType) ? "refresh_token" : opt.GrantType,
            ["client_id"]     = opt.ClientId,
            ["refresh_token"] = refreshToken!,
        };
        if (!string.IsNullOrWhiteSpace(opt.ClientSecret))
            payload["client_secret"] = opt.ClientSecret!;
        if (!string.IsNullOrWhiteSpace(opt.Scope))
            payload["scope"] = opt.Scope!;
        foreach (var kv in opt.ExtraTokenRequestParameters)
            payload[kv.Key] = kv.Value;

        var http = _httpFactory.CreateClient(TokenHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, opt.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(payload),
        };

        _log.LogDebug("OIDC token-refresh → POST {Endpoint} grant={Grant} client={ClientId}",
            opt.TokenEndpoint, payload["grant_type"], opt.ClientId);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("OIDC token-refresh {Status}: {Body}", (int)response.StatusCode, Trim(body));
            throw new VisaryAuthException(
                $"OIDC token-refresh вернул {(int)response.StatusCode}: {Trim(body)}");
        }

        OidcTokenResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OidcTokenResponse>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new VisaryAuthException($"OIDC token-refresh: невалидный JSON: {Trim(body)}", ex);
        }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
            throw new VisaryAuthException("OIDC token-refresh: в ответе нет access_token.");

        var lifetimeSeconds = parsed.ExpiresIn > 0
            ? parsed.ExpiresIn
            : Math.Max(60, opt.FallbackLifetimeSeconds);
        _accessToken     = parsed.AccessToken;
        _accessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds);

        // IdP может выдать НОВЫЙ refresh_token (rotation). Если не сохранить — следующий
        // рефреш пойдёт по старому, IdP может вернуть invalid_grant → backend встанет.
        if (!string.IsNullOrWhiteSpace(parsed.RefreshToken) && parsed.RefreshToken != refreshToken)
        {
            try
            {
                await _refreshStore.SetAsync(parsed.RefreshToken!, ct).ConfigureAwait(false);
                _log.LogInformation("OIDC refresh_token rotated и сохранён в store.");
            }
            catch (Exception ex)
            {
                // Не валим текущий запрос: access_token у нас уже есть. Но это критично —
                // следующий refresh по старому токену скорее всего упадёт. Лог alert-level.
                _log.LogError(ex,
                    "OIDC refresh_token rotation: НЕ удалось записать новый токен в store. " +
                    "Следующий refresh может упасть с invalid_grant.");
            }
        }

        _log.LogInformation(
            "OIDC token-refresh OK: access_token действует до {ExpiresAt:O}, scope='{Scope}'",
            _accessExpiresAt, parsed.Scope);
    }

    public void Dispose() => _gate.Dispose();

    private static string Trim(string s, int max = 500)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class OidcTokenResponse
    {
        [JsonPropertyName("access_token")]  public string  AccessToken  { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")]    public int     ExpiresIn    { get; set; }
        [JsonPropertyName("token_type")]    public string? TokenType    { get; set; }
        [JsonPropertyName("scope")]         public string? Scope        { get; set; }
        [JsonPropertyName("id_token")]      public string? IdToken      { get; set; }
    }
}
