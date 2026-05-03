using Visary.Api.Dto;

namespace Visary.Api.CRUD;

public interface ICrudClient : IDisposable
{
    Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId,
        int finishingMaterialId,
        CancellationToken ct = default);
}
