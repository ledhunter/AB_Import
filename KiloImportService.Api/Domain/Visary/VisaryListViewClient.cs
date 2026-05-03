using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace KiloImportService.Api.Domain.Visary;

public interface IVisaryListViewClient
{
    Task<ListViewResponse<ConstructionProjectRaw>> FetchProjectsAsync(
        string? search,
        int pageSize,
        CancellationToken ct);
}

public sealed class VisaryListViewClient : IVisaryListViewClient
{
    private const string ProjectMnemonic = "constructionproject";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly VisaryApiOptions _options;
    private readonly ILogger<VisaryListViewClient> _log;

    public VisaryListViewClient(
        HttpClient http,
        IOptions<VisaryApiOptions> options,
        ILogger<VisaryListViewClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<ListViewResponse<ConstructionProjectRaw>> FetchProjectsAsync(
        string? search, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException(
                "Visary:BaseUrl не задан в конфигурации. См. appsettings.json.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException(
                "Visary:BearerToken не задан. Заполни через секреты или переменные окружения.");

        var body = new
        {
            Mnemonic = ProjectMnemonic,
            PageSkip = 0,
            PageSize = pageSize,
            Columns = new[] { "ID", "Title", "IdentifierKK", "IdentifierZPLM", "Hidden" },
            Sorts = (string?)null,
            Hidden = false,
            ExtraFilter = (string?)null,
            SearchString = search ?? string.Empty,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/{ProjectMnemonic}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug("Visary → POST listview/constructionproject search='{Search}'", search);

        using var response = await _http.SendAsync(req, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
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
            .ReadFromJsonAsync<ListViewResponse<ConstructionProjectRaw>>(JsonOptions, ct)
            ?? new ListViewResponse<ConstructionProjectRaw>();

        _log.LogInformation("Visary ← 200 constructionproject search='{Search}': {Count} rows, total={Total}",
            search, parsed.Rows.Count, parsed.TotalRows);

        return parsed;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

}
