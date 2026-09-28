using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// Runs the startup crash-recovery pass exactly once per broker boot (the wiring the pass always
/// lacked: WorkflowCrashRecoverer existed and was tested, but nothing ever invoked it, so the
/// "healed on restart" promise died — as a live incident showed, with a stage run wedged in
/// GatesRunning for 90+ minutes and restart healing nothing because restart called nothing).
/// Hosted services start before Kestrel accepts traffic, so by the time a request can arrive
/// every run has been classified: GatesRunning heals back to Active, a lost mid-prompt turn
/// escalates to Disconnected, and terminal states are left alone.
/// </summary>
public sealed class WorkflowCrashRecoveryService(
    WorkflowCrashRecoverer recoverer,
    ILogger<WorkflowCrashRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await recoverer.RecoverAsync(cancellationToken);
        logger.LogInformation(
            "Crash-recovery pass finished in {ElapsedMs}ms: GatesRunning runs healed to Active, lost-turn " +
            "Active runs escalated to Disconnected.",
            stopwatch.ElapsedMilliseconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
