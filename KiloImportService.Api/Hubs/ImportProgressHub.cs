using Microsoft.AspNetCore.SignalR;

namespace KiloImportService.Api.Hubs;

/// <summary>
/// SignalR-хаб для real-time прогресса импорта.
///
/// Клиент (frontend):
///   const conn = new HubConnectionBuilder().withUrl('/hubs/imports').build();
///   await conn.start();
///   await conn.invoke('JoinSession', sessionId);
///   conn.on('StageStarted',  (e) => …);
///   conn.on('StageProgress', (e) => …);
///   conn.on('StageCompleted',(e) => …);
///   conn.on('SessionStatus', (status) => …);
///
/// Сервер (этот проект) шлёт события через <c>IHubContext&lt;ImportProgressHub&gt;</c>
/// в группу <c>session:{sessionId}</c>.
/// </summary>
public class ImportProgressHub : Hub
{
    private const string GroupPrefix = "session:";

    /// <summary>Подписаться на события одной сессии импорта.</summary>
    public Task JoinSession(string sessionId)
        => Groups.AddToGroupAsync(Context.ConnectionId, GroupPrefix + sessionId);

    /// <summary>Отписаться от сессии.</summary>
    public Task LeaveSession(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupPrefix + sessionId);

    public static string GroupName(Guid sessionId) => GroupPrefix + sessionId;
}
