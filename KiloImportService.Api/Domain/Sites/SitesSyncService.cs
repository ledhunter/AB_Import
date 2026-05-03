using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using Visary.Api;
using Visary.Api.Dto;
using Visary.Api.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiloImportService.Api.Domain.Sites;

public interface ISitesSyncService
{
    Task<bool> SyncAsync(int siteId, CancellationToken ct);
}

public sealed class SitesSyncService : ISitesSyncService
{
    private const string Mnemonic = "constructionsite";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] SiteColumns =
    {
        "ID", "Title", "ConstructionProjectID", "ConstructionPermissionNumber",
        "ConstructionProjectNumber", "StageNumber", "RegionID", "TownID",
        "Address", "Hidden", "Version", "FinishingMaterialId",
    };

    private readonly VisaryDbContext _db;
    private readonly HttpClient _http;
    private readonly VisaryOptions _options;
    private readonly ILogger<SitesSyncService> _log;

    public SitesSyncService(
        VisaryDbContext db,
        HttpClient http,
        IOptions<VisaryOptions> options,
        ILogger<SitesSyncService> log)
    {
        _db = db;
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<bool> SyncAsync(int siteId, CancellationToken ct)
    {
        var site = await FetchSiteAsync(siteId, ct);
        if (site == null)
        {
            throw new KeyNotFoundException($"ConstructionSite with ID={siteId} not found in Visary");
        }

        await UpsertAsync(site, ct);
        return true;
    }

    private async Task<ConstructionSiteRaw?> FetchSiteAsync(int siteId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException(
                "Visary:BaseUrl не задан в конфигурации. См. appsettings.json.");
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
            throw new InvalidOperationException(
                "Visary:BearerToken не задан. Заполни через секреты или переменные окружения.");

        var body = new
        {
            Mnemonic = Mnemonic,
            PageSkip = 0,
            PageSize = 1,
            Columns = SiteColumns,
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

        _log.LogDebug(
            "Visary → POST listview/constructionsite siteId={SiteId} associatedFilter",
            siteId);

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
            .ReadFromJsonAsync<ListViewResponse<ConstructionSiteRaw>>(ct)
            ?? new ListViewResponse<ConstructionSiteRaw>();

        if (parsed.Rows.Count == 0)
        {
            _log.LogWarning("Visary ← 200 listview/constructionsite siteId={SiteId}: no rows", siteId);
            return null;
        }

        _log.LogInformation(
            "Visary ← 200 listview/constructionsite siteId={SiteId}: 1 row",
            siteId);
        return parsed.Rows[0];
    }

    private async Task UpsertAsync(ConstructionSiteRaw raw, CancellationToken ct)
    {
        var existing = await _db.ConstructionSites
            .FirstOrDefaultAsync(s => s.Id == raw.ID, ct);

        var entity = existing ?? new ConstructionSite
        {
            Id = raw.ID,
        };

        entity.Title = string.IsNullOrEmpty(raw.Title) ? $"Site #{raw.ID}" : raw.Title!;
        entity.ConstructionProjectId = raw.ConstructionProjectId;
        entity.ConstructionPermissionNumber = raw.ConstructionPermissionNumber;
        entity.ConstructionProjectNumber = raw.ConstructionProjectNumber;
        entity.StageNumber = raw.StageNumber;
        entity.RegionId = raw.RegionId;
        entity.TownId = raw.TownId;
        entity.Address = raw.Address;
        entity.Hidden = raw.Hidden ?? false;
        entity.Version = raw.Version;
        entity.FinishingMaterialId = raw.FinishingMaterialId;

        if (existing == null)
        {
            _db.ConstructionSites.Add(entity);
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "SitesSyncService.UpsertAsync: siteId={SiteId} operation={Op}",
            raw.ID, existing == null ? "Inserted" : "Updated");
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    public sealed class ConstructionSiteRaw
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public int? ConstructionProjectId { get; set; }
        public string? ConstructionPermissionNumber { get; set; }
        public string? ConstructionProjectNumber { get; set; }
        public string? StageNumber { get; set; }
        public int? RegionId { get; set; }
        public int? TownId { get; set; }
        public string? Address { get; set; }
        public bool? Hidden { get; set; }
        public DateTime? Version { get; set; }
        public int? FinishingMaterialId { get; set; }
    }
}
