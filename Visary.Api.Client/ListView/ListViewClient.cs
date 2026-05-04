using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Visary.Api.Dto;
using Visary.Api.Exceptions;

namespace Visary.Api.ListView;

public interface IListViewClient : IDisposable
{
    Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(
        string? search = null,
        int pageSize = 200,
        CancellationToken ct = default);

    Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
        int projectId,
        CancellationToken ct = default);

    Task<ConstructionSiteRaw?> GetSiteByIdAsync(
        int siteId,
        CancellationToken ct = default);

    Task<ConstructionSiteRaw?> GetSiteByProjectAndIdAsync(
        int projectId,
        int siteId,
        CancellationToken ct = default);
}

public sealed class ListViewClient : IListViewClient
{
    private const string ProjectMnemonic = "constructionproject";
    private const string SiteMnemonic = "constructionsite";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
        string? search, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Visary:BaseUrl не задан.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException("Visary:BearerToken не задан.");

        var body = new
        {
            Mnemonic = ProjectMnemonic,
            PageSkip = 0,
            PageSize = pageSize,
            Columns = new[] { "ID", "Title", "IdentifierKK", "IdentifierZPLM", "Hidden" },
            SearchString = search ?? string.Empty,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/{ProjectMnemonic}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → GET listview/{Mnemonic} search='{Search}'", ProjectMnemonic, search);

        using var response = await _http.SendAsync(req, ct);

        HandleAuthError(response, ct);
        HandleError(response, ct);

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<ConstructionProjectRaw>>(JsonOptions, ct)
            ?? new ListViewResponse<ConstructionProjectRaw>();

        _log.LogInformation("Visary ← 200 listview/{Mnemonic}: {Count} rows, total={Total}",
            ProjectMnemonic, parsed.Data.Count, parsed.Total);

        return parsed;
    }

    public async Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
        int projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Visary:BaseUrl не задан.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException("Visary:BearerToken не задан.");

        var body = new
        {
            Mnemonic = SiteMnemonic,
            PageSkip = 0,
            PageSize = 500,
            Columns = new[]
            {
                "ID", "Title", "ConstructionProjectId",
                "ConstructionPermissionNumber", "ConstructionProjectNumber",
                "RegionId", "TownId", "Address",
                "Hidden", "Version", "FinishingMaterialId",
            },
            SearchPhrase = (string?)null,
            Summaries = Array.Empty<object>(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/{SiteMnemonic}/onetomany/Project?associationId={projectId}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → GET listview/{Mnemonic}/onetomany/Project projectId={ProjectId}", SiteMnemonic, projectId);

        using var response = await _http.SendAsync(req, ct);

        HandleAuthError(response, ct);
        HandleError(response, ct);

        var parsed = await response.Content
            .ReadFromJsonAsync<ListViewResponse<ConstructionSiteRaw>>(JsonOptions, ct)
            ?? new ListViewResponse<ConstructionSiteRaw>();

        _log.LogInformation("Visary ← 200 listview/{Mnemonic}/onetomany/Project projectId={ProjectId}: {Count} rows",
            SiteMnemonic, projectId, parsed.Data.Count);

        return parsed;
    }

    public Task<ConstructionSiteRaw?> GetSiteByIdAsync(int siteId, CancellationToken ct)
    {
        throw new NotSupportedException(
            "GetSiteByIdAsync не поддерживается: используйте GetSiteByProjectAndIdAsync(projectId, siteId).");
    }

    public async Task<ConstructionSiteRaw?> GetSiteByProjectAndIdAsync(int projectId, int siteId, CancellationToken ct)
    {
        var response = await GetSitesByProjectAsync(projectId, ct);
        return response.Data.FirstOrDefault(s => s.ID == siteId);
    }

    private void HandleAuthError(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            var body = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary auth error {Status}: {Body}", (int)response.StatusCode, body);
            throw new VisaryAuthException($"Visary вернул {(int)response.StatusCode} — токен истёк.");
        }
    }

    private void HandleError(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = SafeReadBodyAsync(response, ct).GetAwaiter().GetResult();
            _log.LogError("Visary error {Status}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException($"Visary вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
