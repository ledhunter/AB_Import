using Visary.Api.CRUD;
using Visary.Api.ListView;

namespace Visary.Api;

public interface IVisaryClient : IDisposable
{
    IListViewClient ListView { get; }
    ICrudClient Crud { get; }
    
    VisaryOptions Options { get; }
    
    Task EnsureConnectedAsync(CancellationToken ct = default);
}
