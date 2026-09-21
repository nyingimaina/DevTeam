using DevTeam.Broker.Domain;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// Startup crash-recovery pass (Part: crash resilience). The broker writes eager checkpoints
/// (ReleaseStageRun.CheckpointJson) while a stage runs; after a restart this pass walks every
/// stage run and classifies what each one's last checkpoint means:
///   - GatesRunning  → the run was mid-gates when the broker died. Healed back to Active
///                     (phase stays Gates) with a single system guidance note, so RunGatesAsync
///                     re-entry can finish it on the same row.
///   - Active        → the run's live turn is at stake. If there IS a linked ACP session but no
///                     PromptSucceeded checkpoint, the session is unrecoverable (the agent was
///                     mid-turn when we died), so the run is surfaced as Escalated/Disconnected
///                     for a human to act on. Everything else is left alone.
///   - every other status (Complete/BlockedGate/BlockedSignoff/Escalated/Stale/BlockedEntry)
///                     is terminal for this pass and untouched.
/// Idempotent by construction: healing moves GatesRunning → Active and escalation moves
/// Active → Escalated, so a second pass finds nothing left to act on.
/// </summary>
public sealed class WorkflowCrashRecoverer
{
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;

    public WorkflowCrashRecoverer(IDbContextFactory<DevTeamDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var runs = await db.ReleaseStageRuns
            .Include(sr => sr.GuidanceNotes)
            .ToListAsync(ct);

        foreach (var run in runs)
        {
            switch (run.Status)
            {
                case ReleaseStageStatus.GatesRunning:
                    HealGatesRunningRun(run, db);
                    break;
                case ReleaseStageStatus.Active:
                    // A healed GatesRunning run lands here with Phase still Gates; escalation is
                    // about a LOST live prompt, and a gates-phase run carries no conversational
                    // turn — it is re-runnable through RunGatesAsync's re-entry instead.
                    if (run.Phase == StagePhase.Gates) continue;
                    EscalateIfTurnWasLost(run);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static void HealGatesRunningRun(ReleaseStageRun run, DevTeamDbContext db)
    {
        run.Status = ReleaseStageStatus.Active;
        run.ReadyToProceed = false;
        run.FinishedAt = null;

        // Only ever one note per interrupted pass — idempotency against a run that was healed,
        // then somehow landed back in GatesRunning before the next restart.
        if (run.GuidanceNotes.Any(n => n.AddedBy == "system" && n.Text.Contains("interrupted", StringComparison.OrdinalIgnoreCase)))
            return;

        db.ReleaseGuidanceNotes.Add(new ReleaseGuidanceNote
        {
            StageRunId = run.Id,
            StageRun = run,
            Text = "This stage was interrupted mid-gates by a broker restart. It has been " +
                   "returned to Active — re-run gates to finish the pass.",
            AddedBy = "system",
        });
    }

    private static void EscalateIfTurnWasLost(ReleaseStageRun run)
    {
        // Without a linked session there's no turn that could have been lost mid-flight — the
        // run simply hasn't started one, and leaving it Active lets StartStageAsync pick it up
        // the next time the human interacts.
        if (run.AcpSessionId is null)
            return;

        var checkpoint = StageCheckpoint.TryDeserialize(run.CheckpointJson);
        var turnWasLost = checkpoint is null || checkpoint.Signal == StageCheckpointSignal.PromptInProgress;

        // PromptSucceeded (and Idle/Gates) mean no prompt was in flight when we died — the
        // session is either usable or mended on the next interaction, so the run stays Active.
        if (!turnWasLost)
            return;

        run.Status = ReleaseStageStatus.Escalated;
        run.LastErrorKind = StageErrorKind.Disconnected;
        run.LastErrorMessage = "The broker restarted mid-prompt and the agent session was lost.";
        run.LastErrorAt = DateTimeOffset.UtcNow;
    }
}