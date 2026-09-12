using Microsoft.AspNetCore.SignalR;

namespace DevTeam.Broker.Server;

/// <summary>Abstraction over the SignalR hub so the coordinator stays unit-testable.</summary>
public interface IEventBroadcaster
{
    Task BroadcastAsync(StreamEvent streamEvent, CancellationToken cancellationToken);
}

public sealed class SignalRHubBroadcaster : IEventBroadcaster
{
    private readonly IHubContext<BrokerHub, IHubClient> _hub;

    public SignalRHubBroadcaster(IHubContext<BrokerHub, IHubClient> hub) => _hub = hub;

    public async Task BroadcastAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
    {
        await _hub.Clients
            .Group(BrokerHub.SessionGroup(streamEvent.SessionId))
            .OnEvent(streamEvent);
    }
}