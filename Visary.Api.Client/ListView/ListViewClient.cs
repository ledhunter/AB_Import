using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Visary.Api.ListView;

using Dto;
using Exceptions;

public sealed class ListViewClient : IListViewClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] ProjectColumns =
    {
        "ID", "IdentifierKK", "IdentifierZPLM", "Title", "Type", "Phase",
        "Region", "Town", "Developer", "ProjectManagment", "Sponsor",
        "ProjectPeriod", "RowVersion",
    };

    private static readonly string[] SiteColumns =
    {
        "ID", "Title", "ConstructionProjectID", "ConstructionPermissionNumber",
        "ConstructionProjectNumber", "StageNumber", "RegionID", "TownID",
        "Address", "Hidden", "Version", "FinishingMaterialId",
    };

    private readonly HttpClient _http;
    private readonly VisaryOptions _options;
    private readonly ILogger<ListViewClient> _log;

    public ListViewClient(
        HttpClient http,
        IOptions<VisaryOptions> options,
        ILogger<ListViewClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(
        string? search = null,
        int pageSize = 200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException(
                "Visary:BaseUrl не задан в конфигурации. См. appsettings.json.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException(
                "Visary:BearerToken не задан. Заполни через секреты или переменные окружения.");

        var body = new
        {
            Mnemonic = "constructionproject",
            PageSkip = 0,
            PageSize = pageSize,
            Columns = ProjectColumns,
            Sorts = "[{\"selector\":\"ID\",\"desc\":true}]",
            Hidden = false,
            ExtraFilter = (string?)null,
            SearchString = search ?? string.Empty,
            AssociatedID = (int?)null,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/constructionproject")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug(
            "Visary → POST listview/constructionproject pageSize={Size} search='{Search}'",
            pageSize, search);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(req, ct);
        sw.Stop();

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body401 = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary auth error {Status} ({Ms}ms): {Body}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, body401);
            throw new VisaryAuthException(
                $"Visary вернул {(int)response.StatusCode} — токен истёк или невалиден.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var bodyErr = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary error {Status} ({Ms}ms): {Body}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, bodyErr);
            throw new HttpRequestException(
                $"Visary ListView вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<ConstructionProjectRaw>>(Json, ct)
            ?? new ListViewResponse<ConstructionProjectRaw>();

        _log.LogInformation(
            "Visary ← 200 listview/constructionproject ({Ms}ms): {Rows} of {Total}",
            sw.ElapsedMilliseconds, parsed.Rows.Count, parsed.TotalRows);

        return parsed;
    }

    public async Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
        int projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException(
                "Visary:BaseUrl не задан в конфигурации. См. appsettings.json.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException(
                "Visary:BearerToken не задан. Заполни через секреты или переменные окружения.");

        var body = new
        {
            Mnemonic = "constructionsite",
            PageSkip = 0,
            PageSize = 100,
            Columns = SiteColumns,
            Sorts = (string?)null,
            Hidden = false,
            ExtraFilter = (string?)null,
            SearchString = string.Empty,
            AssociatedID = projectId,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/constructionsite")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → POST listview/constructionsite projectId={ProjectId}", projectId);

        using var response = await _http.SendAsync(req, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body401 = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary auth error {Status}: {Body}",
                (int)response.StatusCode, body401);
            throw new VisaryAuthException(
                $"Visary вернул {(int)response.StatusCode} — токен истёк или невалиден.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var bodyErr = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary error {Status}: {Body}",
                (int)response.StatusCode, bodyErr);
            throw new HttpRequestException(
                $"Visary ListView вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<ConstructionSiteRaw>>(Json, ct)
            ?? new ListViewResponse<ConstructionSiteRaw>();

        _log.LogInformation(
            "Visary ← 200 listview/constructionsite projectId={ProjectId}: {Rows} rows",
            projectId, parsed.Rows.Count);

        return parsed;
    }

    public async Task<ConstructionSiteRaw?> GetSiteByIdAsync(
        int siteId,
        CancellationToken ct = default)
    {
        var sites = await GetSitesByProjectAsync(siteId, ct);
        
        if (sites.Rows.Count == 0)
            return null;

        return sites.Rows[0];
    }

    public void Dispose()
    {
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }
}
