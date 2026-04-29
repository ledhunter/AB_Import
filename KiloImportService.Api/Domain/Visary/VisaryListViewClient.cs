using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace KiloImportService.Api.Domain.Visary;

/// <summary>
/// HTTP-клиент к Visary ListView API. Зеркалит контракт фронта
/// (<c>KiloImportService.Web/src/services/listView/createListViewService.ts</c>).
///
/// Используется <see cref="Domain.Projects.ProjectsCacheService"/> для bulk-sync
/// и fallback-поиска по строке ввода.
/// </summary>
public interface IVisaryListViewClient
{
    Task<ListViewResponse<ConstructionProjectRaw>> FetchProjectsAsync(
        int pageSkip,
        int pageSize,
        string searchString,
        CancellationToken ct);
}

public sealed class VisaryListViewClient : IVisaryListViewClient
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
        int pageSkip,
        int pageSize,
        string searchString,
        CancellationToken ct)
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
            PageSkip = pageSkip,
            PageSize = pageSize,
            Columns = ProjectColumns,
            Sorts = "[{\"selector\":\"ID\",\"desc\":true}]",
            Hidden = false,
            ExtraFilter = (string?)null,
            SearchString = searchString ?? string.Empty,
            AssociatedID = (int?)null,
        };

        // ⚠️ Реальный путь Visary — `/api/visary/listview/{mnemonic}`, без префикса
        // `/visary` сервер отдаёт 405. Frontend проксирует через тот же путь
        // (см. KiloImportService.Web/vite.config.ts → /api/visary).
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/api/visary/listview/constructionproject")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        _log.LogDebug(
            "Visary → POST listview/constructionproject pageSkip={Skip} pageSize={Size} search='{Search}'",
            pageSkip, pageSize, searchString);

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

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }
}

public sealed class VisaryAuthException : Exception
{
    public VisaryAuthException(string message) : base(message) { }
}
