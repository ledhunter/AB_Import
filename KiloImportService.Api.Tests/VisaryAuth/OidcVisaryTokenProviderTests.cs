using System.Net;
using System.Text;
using KiloImportService.Api.Tests.VisaryClients;
using Microsoft.Extensions.Logging.Abstractions;
using Visary.Api.Auth;
using Visary.Api.Exceptions;

namespace KiloImportService.Api.Tests.VisaryAuth;

/// <summary>
/// Тесты <see cref="OidcVisaryTokenProvider"/>: кэш, single-flight, rotation refresh_token,
/// invalidation, ошибки IdP. См. doc_project/107-visary-token-provider.md.
/// </summary>
public sealed class OidcVisaryTokenProviderTests
{
    [Fact]
    public async Task First_call_refreshes_and_returns_access_token()
    {
        var (provider, idp, store) = NewProvider();
        idp.EnqueueOk("AT-1", "RT-1", expiresIn: 3600);

        var token = await provider.GetAccessTokenAsync(default);

        Assert.Equal("AT-1", token);
        Assert.Single(idp.Requests);
        var body = idp.RequestBodies[0]!;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("client_id=visary-ui", body);
        // initial refresh_token из store
        Assert.Contains("refresh_token=RT-INITIAL", body);
    }

    [Fact]
    public async Task Second_call_hits_cache_without_idp_request()
    {
        var (provider, idp, _) = NewProvider();
        idp.EnqueueOk("AT-1", "RT-1", expiresIn: 3600);

        _ = await provider.GetAccessTokenAsync(default);
        _ = await provider.GetAccessTokenAsync(default);
        _ = await provider.GetAccessTokenAsync(default);

        // Только один поход в IdP, остальное — cache-hit.
        Assert.Single(idp.Requests);
    }

    [Fact]
    public async Task Expired_access_token_triggers_refresh()
    {
        var (provider, idp, _) = NewProvider(refreshSkewSec: 60);
        // expires_in=10 + skew=60 → токен СРАЗУ протух с точки зрения провайдера.
        idp.EnqueueOk("AT-1", "RT-1", expiresIn: 10);
        idp.EnqueueOk("AT-2", "RT-2", expiresIn: 3600);

        var t1 = await provider.GetAccessTokenAsync(default);
        var t2 = await provider.GetAccessTokenAsync(default);

        Assert.Equal("AT-1", t1);
        Assert.Equal("AT-2", t2);
        Assert.Equal(2, idp.Requests.Count);
    }

    [Fact]
    public async Task Refresh_token_rotation_persists_new_token_to_store()
    {
        var (provider, idp, store) = NewProvider(refreshSkewSec: 60);
        idp.EnqueueOk("AT-1", "RT-NEW", expiresIn: 10);
        idp.EnqueueOk("AT-2", "RT-NEWER", expiresIn: 3600);

        await provider.GetAccessTokenAsync(default);
        // После первого рефреша store должен содержать новый refresh_token.
        Assert.Equal("RT-NEW", await store.GetAsync(default));

        await provider.GetAccessTokenAsync(default);
        // На втором рефреше — отправили в IdP уже ротированный RT-NEW.
        Assert.Contains("refresh_token=RT-NEW", idp.RequestBodies[1]!);
        Assert.Equal("RT-NEWER", await store.GetAsync(default));
    }

    [Fact]
    public async Task Invalidate_forces_next_call_to_refresh()
    {
        var (provider, idp, _) = NewProvider();
        idp.EnqueueOk("AT-1", "RT-1", expiresIn: 3600);
        idp.EnqueueOk("AT-2", "RT-2", expiresIn: 3600);

        var t1 = await provider.GetAccessTokenAsync(default);
        await provider.InvalidateAsync(default);
        var t2 = await provider.GetAccessTokenAsync(default);

        Assert.Equal("AT-1", t1);
        Assert.Equal("AT-2", t2);
        Assert.Equal(2, idp.Requests.Count);
    }

    [Fact]
    public async Task SingleFlight_under_parallel_load_results_in_one_idp_request()
    {
        var (provider, idp, _) = NewProvider();
        idp.EnqueueOk("AT-1", "RT-1", expiresIn: 3600, delayMs: 50);

        // 20 параллельных запросов на холодный кэш — должно быть РОВНО одно обращение в IdP.
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => provider.GetAccessTokenAsync(default))
            .ToArray();
        var tokens = await Task.WhenAll(tasks);

        Assert.All(tokens, t => Assert.Equal("AT-1", t));
        Assert.Single(idp.Requests);
    }

    [Fact]
    public async Task IdP_400_throws_VisaryAuthException()
    {
        var (provider, idp, _) = NewProvider();
        idp.EnqueueError(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");

        var ex = await Assert.ThrowsAsync<VisaryAuthException>(
            () => provider.GetAccessTokenAsync(default));
        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid_grant", ex.Message);
    }

    [Fact]
    public async Task Empty_refresh_token_in_store_throws_with_actionable_message()
    {
        var (provider, _, store) = NewProvider(initialRefreshToken: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAccessTokenAsync(default));
        Assert.Contains("refresh_token пуст", ex.Message);
        Assert.Contains("doc_project/107", ex.Message);
    }

    [Fact]
    public async Task ExtraTokenRequestParameters_propagated_to_payload()
    {
        var (provider, idp, _) = NewProvider(configure: opt =>
        {
            opt.ExtraTokenRequestParameters["resource"] = "https://visary.api";
            opt.ExtraTokenRequestParameters["audience"] = "visary-backend";
        });
        idp.EnqueueOk("AT-1", "RT-1", expiresIn: 3600);

        await provider.GetAccessTokenAsync(default);

        var body = idp.RequestBodies[0]!;
        Assert.Contains("resource=https%3A%2F%2Fvisary.api", body);
        Assert.Contains("audience=visary-backend", body);
    }

    [Fact]
    public async Task Fallback_lifetime_used_when_expires_in_missing()
    {
        var (provider, idp, _) = NewProvider(refreshSkewSec: 60, configure: opt =>
        {
            opt.FallbackLifetimeSeconds = 600;
        });
        // expires_in отсутствует — должен примениться FallbackLifetimeSeconds (600).
        idp.EnqueueOk("AT-1", "RT-1", expiresIn: 0);
        idp.EnqueueOk("AT-2", "RT-2", expiresIn: 3600);

        var t1 = await provider.GetAccessTokenAsync(default);
        // 600-60=540s остаётся, второй вызов должен попасть в кэш.
        var t2 = await provider.GetAccessTokenAsync(default);

        Assert.Equal("AT-1", t1);
        Assert.Equal("AT-1", t2);
        Assert.Single(idp.Requests);
    }

    // ─── Test infra ───────────────────────────────────────────────────────

    private static (OidcVisaryTokenProvider Provider, StubIdpHandler Idp, IVisaryRefreshTokenStore Store)
        NewProvider(int refreshSkewSec = 60,
                    string? initialRefreshToken = "RT-INITIAL",
                    Action<VisaryAuthOptions>? configure = null)
    {
        var idp = new StubIdpHandler();
        var factory = new SingleClientFactory(new HttpClient(idp));

        var store = new InMemoryRefreshTokenStore();
        if (initialRefreshToken is not null)
            store.SetAsync(initialRefreshToken, default).GetAwaiter().GetResult();

        var opt = new VisaryAuthOptions
        {
            TokenEndpoint           = "https://idp.test/oidc/connect/token",
            ClientId                = "visary-ui",
            GrantType               = "refresh_token",
            RefreshSkewSeconds      = refreshSkewSec,
            FallbackLifetimeSeconds = 300,
        };
        configure?.Invoke(opt);

        var provider = new OidcVisaryTokenProvider(
            factory,
            new TestOptionsMonitor<VisaryAuthOptions>(opt),
            store,
            NullLogger<OidcVisaryTokenProvider>.Instance);
        return (provider, idp, store);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) { _client = client; }
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class InMemoryRefreshTokenStore : IVisaryRefreshTokenStore
    {
        private string? _token;
        public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(_token);
        public Task SetAsync(string refreshToken, CancellationToken ct = default)
        {
            _token = refreshToken;
            return Task.CompletedTask;
        }
    }

    private sealed class StubIdpHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> RequestBodies { get; } = new();
        private readonly Queue<(HttpStatusCode Status, string Body, int DelayMs)> _responses = new();

        public StubIdpHandler EnqueueOk(string accessToken, string refreshToken, int expiresIn, int delayMs = 0)
        {
            var json = expiresIn > 0
                ? $"{{\"access_token\":\"{accessToken}\",\"refresh_token\":\"{refreshToken}\",\"expires_in\":{expiresIn},\"token_type\":\"Bearer\",\"scope\":\"openid\"}}"
                : $"{{\"access_token\":\"{accessToken}\",\"refresh_token\":\"{refreshToken}\",\"token_type\":\"Bearer\",\"scope\":\"openid\"}}";
            _responses.Enqueue((HttpStatusCode.OK, json, delayMs));
            return this;
        }

        public StubIdpHandler EnqueueError(HttpStatusCode status, string body)
        {
            _responses.Enqueue((status, body, 0));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(ct));
            if (_responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            var (status, body, delay) = _responses.Dequeue();
            if (delay > 0) await Task.Delay(delay, ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}

