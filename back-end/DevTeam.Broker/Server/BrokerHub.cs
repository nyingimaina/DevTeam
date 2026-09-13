using Microsoft.AspNetCore.SignalR;

namespace DevTeam.Broker.Server;

public interface IHubClient
{
    Task OnEvent(StreamEvent evt);
}

public sealed class BrokerHub : Hub<IHubClient>
{
    public Task JoinSession(string sessionId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));

    public Task LeaveSession(string sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));

    public Task JoinRelease(string releaseId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, ReleaseGroup(releaseId));

    public Task LeaveRelease(string releaseId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ReleaseGroup(releaseId));

    internal static string SessionGroup(string sessionId) => $"session:{sessionId}";
    internal static string ReleaseGroup(string releaseId) => $"release:{releaseId}";
}