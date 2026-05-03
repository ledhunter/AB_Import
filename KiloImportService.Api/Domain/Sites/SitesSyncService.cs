using System.Net.Http.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Visary.Api;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;

namespace KiloImportService.Api.Domain.Sites;

public interface ISitesSyncService
{
    Task<bool> SyncAsync(int siteId, CancellationToken ct);
    Task<bool> UpdateSiteFinishingMaterialAsync(int siteId, int finishingMaterialId, CancellationToken ct);
}

public sealed class SitesSyncService : ISitesSyncService
{
    private const string Mnemonic = "constructionsite";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly VisaryDbContext _db;
    private readonly ICrudClient _visaryClient;
    private readonly global::Visary.Api.Dto.VisaryOptions _options;
    private readonly ILogger<SitesSyncService> _log;

    public SitesSyncService(
        VisaryDbContext db,
        ICrudClient visaryClient,
        IOptions<global::Visary.Api.Dto.VisaryOptions> options,
        ILogger<SitesSyncService> log)
    {
        _db = db;
        _visaryClient = visaryClient;
        _options = options.Value;
        _log = log;
    }

    public async Task<bool> SyncAsync(int siteId, CancellationToken ct)
    {
        var client = GetListViewClient();
        var siteData = await client.GetSiteByIdAsync(siteId, ct);
        if (siteData == null)
            throw new KeyNotFoundException($"ConstructionSite with ID={siteId} not found in Visary");

        await UpsertAsync(siteData, ct);
        return true;
    }

    public async Task<bool> UpdateSiteFinishingMaterialAsync(int siteId, int finishingMaterialId, CancellationToken ct)
    {
        return await _visaryClient.UpdateSiteFinishingMaterialAsync(siteId, finishingMaterialId, ct);
    }

    private async Task UpsertAsync(global::Visary.Api.Dto.ConstructionSiteRaw raw, CancellationToken ct)
    {
        var existing = await _db.ConstructionSites
            .FirstOrDefaultAsync(s => s.Id == raw.ID, ct);

        var entity = existing ?? new ConstructionSite { Id = raw.ID };

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
            _db.ConstructionSites.Add(entity);

        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "SitesSyncService.UpsertAsync: siteId={SiteId} operation={Op}",
            raw.ID, existing == null ? "Inserted" : "Updated");
    }

    private global::Visary.Api.ListView.IListViewClient GetListViewClient()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        return new global::Visary.Api.ListView.ListViewClient(
            new System.Net.Http.HttpClient(),
            Microsoft.Extensions.Options.Options.Create(_options),
            loggerFactory.CreateLogger<global::Visary.Api.ListView.ListViewClient>());
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
