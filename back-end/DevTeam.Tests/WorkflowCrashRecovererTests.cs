using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests;

// Verifies the startup crash-recovery pass (Part: crash resilience). The broker writes eager
// checkpoints while a stage runs; if the broker dies mid-turn those checkpoints are exactly
// what distinguishes "the last prompt finished" (safe to leave Active) from "the agent was in
// the middle of a turn when we died" (the ACP session is gone, so the run must be surfaced as
// Escalated so the human knows and can act) — and a run wedged in GatesRunning with no process
// left to finish it has to be handed back to a re-runnable state.
public class WorkflowCrashRecovererTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public WorkflowCrashRecovererTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> CreateFactory()
        => new ResilienceDbContextFactory(_connection);

    private sealed class ResilienceDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    private static StageCheckpoint Checkpoint(StageCheckpointSignal signal) =>
        new(signal, StagePhase.GuidedQA, DateTimeOffset.UtcNow);

    private async Task<Guid> SeedFeatureWithSingleRunAsync(
        ReleaseStageStatus status,
        StagePhase phase,
        StageCheckpointSignal? checkpointSignal = null,
        bool withAcpSession = true)
    {
        await using var db = CreateFactory().CreateDbContext();
        var release = new DevTeamRelease { WorkspacePath = @"C:\work\proj" };
        var feature = new ReleaseFeature
        {
            Release = release,
            Key = "feat-001",
            Title = "Login",
            Status = ReleaseFeatureStatus.InProgress,
        };
        feature.FlowPosition = new ReleaseFlowPosition
        {
            Feature = feature,
            CurrentStageIndex = 0,
            CurrentStageName = "business-analyst",
        };
        feature.StageRuns.Add(new ReleaseStageRun
        {
            Feature = feature,
            StageName = "business-analyst",
            Status = status,
            Phase = phase,
            AcpSessionId = withAcpSession ? Guid.NewGuid().ToString() : null,
            CheckpointJson = checkpointSignal is { } signal ? Checkpoint(signal).Serialize() : null,
        });
        release.Features.Add(feature);
        db.Releases.Add(release);
        await db.SaveChangesAsync();
        return feature.Id;
    }

    [Fact]
    public async Task Recover_GatesRunningRun_HealsToActiveKeepsGatesPhaseAndNotesOnce()
    {
        var featureId = await SeedFeatureWithSingleRunAsync(ReleaseStageStatus.GatesRunning, StagePhase.Gates);
        var recoverer = new WorkflowCrashRecoverer(CreateFactory());

        await recoverer.RecoverAsync(CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Include(r => r.GuidanceNotes).Single(r => r.ReleaseFeatureId == featureId);
        Assert.Equal(ReleaseStageStatus.Active, run.Status);
        Assert.Equal(StagePhase.Gates, run.Phase);
        Assert.False(run.ReadyToProceed);
        Assert.Null(run.FinishedAt);
        var note = Assert.Single(run.GuidanceNotes);
        Assert.Contains("interrupted", note.Text);
        Assert.Equal("system", note.AddedBy);
    }

    [Fact]
    public async Task Recover_ActiveRunWithoutFinishedPromptCheckpoint_EscalatesAsDisconnected()
    {
        var featureId = await SeedFeatureWithSingleRunAsync(
            ReleaseStageStatus.Active, StagePhase.GuidedQA, checkpointSignal: null);
        var recoverer = new WorkflowCrashRecoverer(CreateFactory());

        await recoverer.RecoverAsync(CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId);
        Assert.Equal(ReleaseStageStatus.Escalated, run.Status);
        Assert.Equal(StageErrorKind.Disconnected, run.LastErrorKind);
        Assert.NotNull(run.LastErrorAt);
    }

    [Fact]
    public async Task Recover_ActiveRunMidPrompt_EscalatesAsDisconnected()
    {
        var featureId = await SeedFeatureWithSingleRunAsync(
            ReleaseStageStatus.Active, StagePhase.GuidedQA, StageCheckpointSignal.PromptInProgress);
        var recoverer = new WorkflowCrashRecoverer(CreateFactory());

        await recoverer.RecoverAsync(CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId);
        Assert.Equal(ReleaseStageStatus.Escalated, run.Status);
        Assert.Equal(StageErrorKind.Disconnected, run.LastErrorKind);
        Assert.NotNull(run.LastErrorAt);
    }

    [Fact]
    public async Task Recover_ActiveRunWithFinishedPromptCheckpoint_IsLeftAlone()
    {
        var featureId = await SeedFeatureWithSingleRunAsync(
            ReleaseStageStatus.Active, StagePhase.GuidedQA, StageCheckpointSignal.PromptSucceeded);
        var recoverer = new WorkflowCrashRecoverer(CreateFactory());

        await recoverer.RecoverAsync(CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Include(r => r.GuidanceNotes).Single(r => r.ReleaseFeatureId == featureId);
        Assert.Equal(ReleaseStageStatus.Active, run.Status);
        Assert.Equal(StageErrorKind.None, run.LastErrorKind);
        Assert.Empty(run.GuidanceNotes);
    }

    [Fact]
    public async Task Recover_ActiveRunWithNoSession_IsLeftAlone()
    {
        var featureId = await SeedFeatureWithSingleRunAsync(
            ReleaseStageStatus.Active, StagePhase.GuidedQA, checkpointSignal: null, withAcpSession: false);
        var recoverer = new WorkflowCrashRecoverer(CreateFactory());

        await recoverer.RecoverAsync(CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId);
        Assert.Equal(ReleaseStageStatus.Active, run.Status);
    }

    [Theory]
    [InlineData(ReleaseStageStatus.Complete)]
    [InlineData(ReleaseStageStatus.BlockedGate)]
    [InlineData(ReleaseStageStatus.BlockedSignoff)]
    [InlineData(ReleaseStageStatus.Escalated)]
    [InlineData(ReleaseStageStatus.Stale)]
    [InlineData(ReleaseStageStatus.BlockedEntry)]
    public async Task Recover_LeavesTerminalStatesUntouched(ReleaseStageStatus status)
    {
        var featureId = await SeedFeatureWithSingleRunAsync(status, StagePhase.GuidedQA);
        var recoverer = new WorkflowCrashRecoverer(CreateFactory());

        await recoverer.RecoverAsync(CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Include(r => r.GuidanceNotes).Single(r => r.ReleaseFeatureId == featureId);
        Assert.Equal(status, run.Status);
        Assert.Empty(run.GuidanceNotes);
    }

    [Fact]
    public async Task Recover_IsIdempotent_DoesNotDuplicatNotesNorRewriteState()
    {
        var featureId = await SeedFeatureWithSingleRunAsync(ReleaseStageStatus.GatesRunning, StagePhase.Gates);
        var recoverer = new WorkflowCrashRecoverer(CreateFactory());

        await recoverer.RecoverAsync(CancellationToken.None);
        await recoverer.RecoverAsync(CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Include(r => r.GuidanceNotes).Single(r => r.ReleaseFeatureId == featureId);
        Assert.Equal(ReleaseStageStatus.Active, run.Status);
        Assert.Single(run.GuidanceNotes);
    }
}