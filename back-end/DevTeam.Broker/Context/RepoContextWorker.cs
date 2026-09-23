using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Context;

/// <summary>
/// Drains the coalescing queue in the background. Each workspace refresh is a tracked task, so
/// two different workspaces make progress at the same time while the per-workspace lock inside
/// <see cref="RepoContextService"/> keeps each one's writes serialised. This is the only place
/// allowed to catch-all, and the only failure a refresh can have for the user is "the old
/// overview stayed" (spec REQ-005/REQ-007).
/// </summary>
public sealed class RepoContextWorker : BackgroundService
{
    private readonly RepoContextQueue _queue;
    private readonly IRepoContextService _service;
    private readonly ILogger<RepoContextWorker>? _logger;

    public RepoContextWorker(
        RepoContextQueue queue,
        IRepoContextService service,
        ILogger<RepoContextWorker>? logger = null)
    {
        _queue = queue;
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RepoContextWork? work;
                try
                {
                    work = await _queue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (work is null)
                    continue;

                running.RemoveAll(task => task.IsCompleted);
                running.Add(RunOneAsync(work, stoppingToken));
            }
        }
        finally
        {
            await Task.WhenAll(running);
        }
    }

    private async Task RunOneAsync(RepoContextWork work, CancellationToken stoppingToken)
    {
        try
        {
            await _service.RefreshAsync(work.WorkspacePath, work.Trigger, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger?.LogDebug("Code overview refresh cancelled during shutdown for {Workspace}", work.WorkspacePath);
        }
        catch (Exception ex)
        {
            // A single bad refresh must never stop the worker from handling the next request.
            _logger?.LogError(ex, "Code overview worker errored for {Workspace}", work.WorkspacePath);
        }
    }
}
