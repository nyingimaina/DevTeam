using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SemaNami.Core.Conversations;

namespace DevTeam.Broker.SemaNami;

/// <summary>
/// Runs the live SemaNami Telegram channel: one loop long-polls Telegram and durably persists
/// whatever arrives (ConversationListener.PollOnceAsync — 30s per cycle, looped back-to-back for
/// near-continuous responsiveness), a second loop waits for the next message on the channel's one
/// MVP conversation and hands it to SemaNamiReplyRouter. Registered the same way as
/// RepoContextWorker/MetricsRetentionService (see Program.cs). Both loops are public so they're
/// directly testable with a cancellation token, without waiting on BackgroundService's own
/// start/stop lifecycle.
/// </summary>
public sealed class SemaNamiListenerService : BackgroundService
{
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(5);

    private readonly SemaNamiSettings _settings;
    private readonly ConversationListener _listener;
    private readonly IRealtimeNotifier _realtimeNotifier;
    private readonly ISemaNamiReplyRouter _router;
    private readonly ILogger<SemaNamiListenerService> _logger;

    public SemaNamiListenerService(
        SemaNamiSettings settings,
        ConversationListener listener,
        IRealtimeNotifier realtimeNotifier,
        ISemaNamiReplyRouter router,
        ILogger<SemaNamiListenerService> logger)
    {
        _settings = settings;
        _listener = listener;
        _realtimeNotifier = realtimeNotifier;
        _router = router;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _settings.LoadAsync(stoppingToken);
        await Task.WhenAll(PollLoopAsync(stoppingToken), RouteLoopAsync(stoppingToken));
    }

    public async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_settings.Enabled)
            {
                await DelayAsync(DisabledPollInterval, ct);
                continue;
            }

            try
            {
                await _listener.PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A single bad poll (Telegram unreachable, transient network error) must never
                // stop the channel from trying again.
                _logger.LogWarning(ex, "SemaNami poll failed; retrying.");
                await DelayAsync(ErrorBackoff, ct);
            }
        }
    }

    public async Task RouteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_settings.Enabled)
            {
                await DelayAsync(DisabledPollInterval, ct);
                continue;
            }

            try
            {
                var message = await _realtimeNotifier.SubscribeAsync(
                    SemaNamiPlatformNotifier.Sender, SemaNamiChannelState.ConversationId, ct);
                if (message is not null)
                    await _router.RouteAsync(message, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SemaNami reply routing failed; retrying.");
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown/cancellation — the caller's own loop condition handles it.
        }
    }
}
