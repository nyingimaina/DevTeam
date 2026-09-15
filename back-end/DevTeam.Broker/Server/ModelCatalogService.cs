using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Server;

/// <summary>
/// Discovers which AI models the underlying agent is configured with (e.g. OpenCode's
/// default, or Claude/Gemini once the operator authenticates that provider), so the UI
/// can offer a simple picker. The agent's configured models don't change at runtime, so
/// the result of one lightweight session probe is cached for the app's lifetime.
/// </summary>
public sealed class ModelCatalogService
{
    private readonly IWorkflowCoordinator _coordinator;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<ModelOption>? _cached;

    public ModelCatalogService(IWorkflowCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public async Task<IReadOnlyList<ModelOption>> GetAvailableModelsAsync(string workspacePath, CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
            return cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cachedAfterLock)
                return cachedAfterLock;

            var probe = await _coordinator.NewSessionAsync(workspacePath, null, null, cancellationToken);
            _cached = probe.Models;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
