using Visary.Api.Dto;

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
}
