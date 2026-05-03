using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace KiloImportService.Api.Domain.Visary;

/// <summary>
/// HTTP-клиент к Visary CRUD API для обновления объектов строительства.
/// </summary>
public interface IVisarySitesCrudClient
{
    Task<bool> UpdateSiteFinishingMaterialAsync(int siteId, int finishingMaterialId, CancellationToken ct);
}

public sealed class VisarySitesCrudClient : IVisarySitesCrudClient
{
    private const string Mnemonic = "constructionsite";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly VisaryApiOptions _options;
    private readonly ILogger<VisarySitesCrudClient> _log;

    public VisarySitesCrudClient(
        HttpClient http,
        IOptions<VisaryApiOptions> options,
        ILogger<VisarySitesCrudClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<bool> UpdateSiteFinishingMaterialAsync(int siteId, int finishingMaterialId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException(
                "Visary:BaseUrl не задан в конфигурации. См. appsettings.json.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException(
                "Visary:BearerToken не задан. Заполни через секреты или переменные окружения.");

        var siteData = await FetchSiteDataAsync(siteId, ct);
        if (siteData == null)
        {
            throw new KeyNotFoundException($"ConstructionSite with ID={siteId} not found in Visary");
        }

        siteData.FinishingMaterialId = finishingMaterialId;
        siteData.ID = siteId;

        await UpdateSiteAsync(siteData, ct);

        _log.LogInformation(
            "VisarySitesCrudClient.UpdateSiteFinishingMaterialAsync: siteId={SiteId} success",
            siteId);

        return true;
    }

    private async Task<SiteUpdateData?> FetchSiteDataAsync(int siteId, CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = Mnemonic,
            PageSkip = 0,
            PageSize = 1,
            Columns = new[] { "ID", "FinishingMaterialId", "Version" },
            Sorts = (string?)null,
            Hidden = false,
            ExtraFilter = (string?)null,
            SearchString = string.Empty,
            AssociatedID = siteId,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/{Mnemonic}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → GET constructionsite by ID={SiteId}", siteId);

        using var response = await _http.SendAsync(req, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body401 = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary auth error {Status}: {Body}", (int)response.StatusCode, body401);
            throw new VisaryAuthException(
                $"Visary вернул {(int)response.StatusCode} — токен истёк или невалиден.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var bodyErr = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary error {Status}: {Body}", (int)response.StatusCode, bodyErr);
            throw new HttpRequestException(
                $"Visary ListView вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<SiteUpdateData>>(JsonOptions, ct)
            ?? new ListViewResponse<SiteUpdateData>();

        if (parsed.Rows.Count == 0)
        {
            _log.LogWarning("Visary ← 200 constructionsite siteId={SiteId}: no rows", siteId);
            return null;
        }

        _log.LogInformation("Visary ← 200 constructionsite siteId={SiteId}: 1 row", siteId);
        return parsed.Rows[0];
    }

    private async Task UpdateSiteAsync(SiteUpdateData siteData, CancellationToken ct)
    {
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
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → PUT constructionsite ID={SiteId}", siteData.ID);

        using var response = await _http.SendAsync(req, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body401 = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary auth error {Status}: {Body}", (int)response.StatusCode, body401);
            throw new VisaryAuthException(
                $"Visary вернул {(int)response.StatusCode} — токен истёк или невалиден.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body409 = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary conflict error 409: {Body}", body409);
            throw new HttpRequestException(
                $"Visary вернул 409 Conflict — вероятно, Version устарела. SiteId={siteData.ID}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var bodyErr = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary error {Status}: {Body}", (int)response.StatusCode, bodyErr);
            throw new HttpRequestException(
                $"Visary ListView вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        _log.LogInformation("Visary ← 200 PUT constructionsite ID={SiteId}", siteData.ID);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }
}

public sealed class SiteUpdateData
{
    public int ID { get; set; }
    public int? FinishingMaterialId { get; set; }
    public DateTime? Version { get; set; }
}
