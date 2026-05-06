using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Visary.Api.Dto;
using Visary.Api.Exceptions;

namespace Visary.Api.Common;

public abstract class VisaryHttpBase<T> : IDisposable
{
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected readonly HttpClient _http;
    protected readonly VisaryOptions _options;
    protected readonly ILogger<T> _log;

    protected VisaryHttpBase(HttpClient http, IOptions<VisaryOptions> options, ILogger<T> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    protected string BaseUrl => _options.BaseUrl.TrimEnd('/');

    protected void EnsureConfig()
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Visary:BaseUrl не задан.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException("Visary:BearerToken не задан.");
    }

    protected HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        EnsureConfig();
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        return req;
    }

    protected void HandleAuthError(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary auth error {Status}: {Body}", (int)response.StatusCode, body);
            throw new VisaryAuthException($"Visary вернул {(int)response.StatusCode} — токен истёк.");
        }
    }

    protected void HandleConflict(HttpResponseMessage response, CancellationToken ct, string context)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary 409 Conflict [{Context}]: {Body}", context, body);
            throw new HttpRequestException($"Visary вернул 409 Conflict — RowVersion устарел. {context}");
        }
    }

    protected void HandleError(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary error {Status}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException($"Visary вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    protected static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
