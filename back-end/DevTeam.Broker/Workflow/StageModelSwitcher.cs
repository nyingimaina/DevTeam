using DevTeam.Broker.Domain;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Workflow;

/// <summary>What the "switch model" action reports back. <see cref="Message"/> is safe to show a user as-is.</summary>
public sealed record ModelSwitchResult(bool Ok, string Message, string? ModelId);

/// <summary>The two agent operations a model switch needs — kept narrow so it's fakeable.</summary>
public interface IModelSwitchBackend
{
    Task SetModelAsync(Guid sessionId, string modelId, CancellationToken ct);

    /// <summary>Sends a tiny housekeeping prompt and returns once the model answers (throws if it can't).</summary>
    Task ProbeAsync(Guid sessionId, string text, bool isPriming, CancellationToken ct);
}

public sealed class BrokerModelSwitchBackend(BrokerCoordinator coordinator) : IModelSwitchBackend
{
    public Task SetModelAsync(Guid sessionId, string modelId, CancellationToken ct)
        => coordinator.SetModelAsync(sessionId, modelId, ct);

    public Task ProbeAsync(Guid sessionId, string text, bool isPriming, CancellationToken ct)
        => coordinator.PromptWithSessionRecoveryAsync(sessionId, text, ct, isPriming);
}

/// <summary>
/// Recovers a stage whose agent turn was refused by the model provider: selects another model on
/// the stage's live session, then proves it works with a tiny "connection check" prompt. Only when
/// the model actually answers is the stage's failure marker cleared — a model that can't be used
/// leaves the stage exactly as it was (still failed) so the user can pick again.
/// </summary>
public sealed class StageModelSwitcher(
    IDbContextFactory<DevTeamDbContext> dbFactory,
    IModelSwitchBackend backend,
    ILogger<StageModelSwitcher> logger,
    TimeSpan? probeTimeout = null)
{
    public const string ProbePrompt = "Connection check. Reply with the single word OK.";

    private readonly TimeSpan _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(60);

    public async Task<ModelSwitchResult> SwitchAndVerifyAsync(Guid featureId, string modelId, CancellationToken ct)
    {
        Guid sessionId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (!await db.ReleaseFeatures.AnyAsync(f => f.Id == featureId, ct))
                throw new KeyNotFoundException($"Feature {featureId} not found.");

            var run = await FindLiveRunAsync(db, featureId, ct)
                ?? throw new InvalidOperationException("This feature has no running stage to switch the model for.");
            if (run.AcpSessionId is null)
                throw new InvalidOperationException("The stage has no agent session yet.");
            sessionId = Guid.Parse(run.AcpSessionId);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_probeTimeout);
        try
        {
            await backend.SetModelAsync(sessionId, modelId, timeout.Token);
            await backend.ProbeAsync(sessionId, ProbePrompt, isPriming: true, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(modelId, "That model didn't answer in time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Switching feature {FeatureId} to model {Model} failed", featureId, modelId);
            return Failed(modelId, Reason(ex));
        }

        await ClearFailureAsync(featureId, ct);
        logger.LogInformation("Feature {FeatureId} switched to model {Model} and verified", featureId, modelId);
        return new ModelSwitchResult(true, "Connected. Resuming your work with the new model.", modelId);
    }

    private static Task<ReleaseStageRun?> FindLiveRunAsync(DevTeamDbContext db, Guid featureId, CancellationToken ct)
        => db.ReleaseStageRuns
            .Where(r => r.ReleaseFeatureId == featureId &&
                (r.Status == ReleaseStageStatus.Escalated || r.Status == ReleaseStageStatus.Active))
            .OrderByDescending(r => r.Attempt)
            .FirstOrDefaultAsync(ct);

    private async Task ClearFailureAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await FindLiveRunAsync(db, featureId, ct);
        if (run is null) return;

        run.LastErrorKind = StageErrorKind.None;
        run.LastErrorMessage = null;
        run.LastErrorAt = null;
        // Escalated is a "last attempt failed" marker; a model that just answered means the stage is live again.
        if (run.Status == ReleaseStageStatus.Escalated) run.Status = ReleaseStageStatus.Active;
        await db.SaveChangesAsync(ct);
    }

    private static string Reason(Exception ex) => ex switch
    {
        RpcException rpc => $"That model couldn't be used: {rpc.Message.TrimEnd('.', ' ')}.",
        AcpDisconnectedException => "The AI agent stopped while checking that model.",
        _ => "That model couldn't be used.",
    };

    private static ModelSwitchResult Failed(string modelId, string reason)
        => new(false, $"{reason} Please try another.", modelId);
}
