using System;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Visary.Api.Dto;
using Visary.Api.Exceptions;

namespace Visary.Api.CRUD;

public interface ICrudClient : IDisposable
{
    Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId,
        int finishingMaterialId,
        CancellationToken ct = default);
}

public sealed class CrudClient : ICrudClient
{
    private const string Mnemonic = "constructionsite";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly VisaryOptions _options;
    private readonly ILogger<CrudClient> _log;

    public CrudClient(
        HttpClient http,
        IOptions<VisaryOptions> options,
        ILogger<CrudClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId, int finishingMaterialId, CancellationToken ct)
    {
        var siteData = await FetchSiteDataAsync(siteId, ct);
        if (siteData == null)
            throw new KeyNotFoundException($"ConstructionSite with ID={siteId} not found in Visary");

        siteData.FinishingMaterialId = finishingMaterialId;

        await UpdateSiteAsync(siteData, ct);

        _log.LogInformation("CrudClient.UpdateSiteFinishingMaterialAsync: siteId={SiteId} success", siteId);
        return true;
    }

    private async Task<SiteUpdateData?> FetchSiteDataAsync(int siteId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Visary:BaseUrl не задан.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException("Visary:BearerToken не задан.");

        var body = new
        {
            Mnemonic = Mnemonic,
            PageSkip = 0,
            PageSize = 1,
            Columns = new[] { "ID", "FinishingMaterialId", "Version" },
            AssociatedID = siteId,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/{Mnemonic}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → GET constructionsite by ID={SiteId}", siteId);

        using var response = await _http.SendAsync(req, ct);

        HandleAuthError(response, ct);
        HandleError(response, ct);

        var parsed = await response.Content
            .ReadFromJsonAsync<Visary.Api.Dto.ListViewResponse<SiteUpdateData>>(JsonOptions, ct)
            ?? new Visary.Api.Dto.ListViewResponse<SiteUpdateData>();

        if (parsed.Data.Count == 0)
        {
            _log.LogWarning("Visary ← 200 constructionsite siteId={SiteId}: no rows", siteId);
            return null;
        }

        _log.LogInformation("Visary ← 200 constructionsite siteId={SiteId}: 1 row", siteId);
        return parsed.Data[0];
    }

    private async Task UpdateSiteAsync(SiteUpdateData siteData, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Visary:BaseUrl не задан.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException("Visary:BearerToken не задан.");

        var body = new
        {
            Mnemonic = Mnemonic,
            Data = new[] { siteData },
        };

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/{Mnemonic}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → PUT constructionsite ID={SiteId}", siteData.ID);

        using var response = await _http.SendAsync(req, ct);

        HandleAuthError(response, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var body409 = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary conflict error 409: {Body}", body409);
            throw new HttpRequestException($"Visary вернул 409 Conflict — вероятно, Version устарела. SiteId={siteData.ID}");
        }

        HandleError(response, ct);

        _log.LogInformation("Visary ← 200 PUT constructionsite ID={SiteId}", siteData.ID);
    }

    private void HandleAuthError(System.Net.Http.HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            var body = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary auth error {Status}: {Body}", (int)response.StatusCode, body);
            throw new VisaryAuthException($"Visary вернул {(int)response.StatusCode} — токен истёк.");
        }
    }

    private void HandleError(System.Net.Http.HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary error {Status}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException($"Visary вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static async Task<string> SafeReadBodyAsync(System.Net.Http.HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public sealed class SiteUpdateData
    {
        public int ID { get; set; }
        public int? FinishingMaterialId { get; set; }
        public DateTime? Version { get; set; }
    }
}
