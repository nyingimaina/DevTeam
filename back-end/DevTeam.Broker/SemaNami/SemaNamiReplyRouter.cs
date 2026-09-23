using DevTeam.Broker.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemaNami.Core.Conversations;

namespace DevTeam.Broker.SemaNami;

/// <summary>
/// Routes an incoming SemaNami reply to the feature the channel is currently bound to. Kept
/// separate from SemaNamiListenerService so the routing decision is testable without a real
/// BackgroundService loop (mirrors RepoContextWorker delegating to IRepoContextService).
/// IWorkflowEngine is scoped, not singleton, so a scope is created per routed message — the same
/// reason ApiEndpoints resolves it per-request rather than holding one instance.
/// </summary>
public interface ISemaNamiReplyRouter
{
    Task RouteAsync(StoredMessage message, CancellationToken ct);
}

public sealed class SemaNamiReplyRouter : ISemaNamiReplyRouter
{
    private readonly SemaNamiChannelState _channelState;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SemaNamiReplyRouter> _logger;

    public SemaNamiReplyRouter(SemaNamiChannelState channelState, IServiceScopeFactory scopeFactory, ILogger<SemaNamiReplyRouter> logger)
    {
        _channelState = channelState;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RouteAsync(StoredMessage message, CancellationToken ct)
    {
        var featureId = _channelState.BoundFeatureId;
        if (featureId is null)
        {
            _logger.LogDebug("Received a SemaNami reply with no feature bound to the channel; ignoring.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        try
        {
            // Same entry point the web UI's own send-message endpoint uses — a Telegram reply is
            // just another way for the user to answer the BA's question.
            await engine.SendMessageEnforcingSingleQuestionAsync(featureId.Value, message.Text, ct);
        }
        catch (Exception ex)
        {
            // A failed route must never crash the listener loop — the next reply still gets a try.
            _logger.LogWarning(ex, "Failed to route a SemaNami reply to feature {FeatureId}.", featureId);
        }
    }
}
