using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Visary.Api.CRUD;
using Visary.Api.ListView;
using ConstructionSiteRaw = Visary.Api.Dto.ConstructionSiteRaw;

namespace KiloImportService.Api.Domain.Sites;

public interface ISitesSyncService
{
    Task<bool> SyncAsync(int siteId, int projectId, CancellationToken ct);
    Task<bool> UpdateSiteFinishingMaterialAsync(int siteId, int finishingMaterialId, CancellationToken ct);
}

public sealed class SitesSyncService : ISitesSyncService
{
    private readonly VisaryDbContext _db;
    private readonly ICrudClient _crudClient;
    private readonly IListViewClient _listViewClient;
    private readonly ILogger<SitesSyncService> _log;

    public SitesSyncService(
        VisaryDbContext db,
        ICrudClient crudClient,
        IListViewClient listViewClient,
        ILogger<SitesSyncService> log)
    {
        _db = db;
        _crudClient = crudClient;
        _listViewClient = listViewClient;
        _log = log;
    }

    public async Task<bool> SyncAsync(int siteId, int projectId, CancellationToken ct)
    {
        var siteData = await _listViewClient.GetSiteByProjectAndIdAsync(projectId, siteId, ct);
        if (siteData == null)
            throw new KeyNotFoundException($"ConstructionSite with ID={siteId} not found in Visary (projectId={projectId})");

        await UpsertAsync(siteData, ct);
        return true;
    }

    public async Task<bool> UpdateSiteFinishingMaterialAsync(int siteId, int finishingMaterialId, CancellationToken ct)
    {
        return await _crudClient.UpdateSiteFinishingMaterialAsync(siteId, finishingMaterialId, ct);
    }

    private async Task UpsertAsync(ConstructionSiteRaw raw, CancellationToken ct)
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

}
