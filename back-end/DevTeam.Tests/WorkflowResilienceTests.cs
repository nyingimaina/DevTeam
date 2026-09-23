using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

// Re-entrancy + crash-resilience + last-ditch escape hatch for WorkflowEngine (Part: shape of
// the resilience work). A stage that crashed mid-gates or landed BlockedGate/Escalated must be
// re-runnable on the same run row (no phantom attempts, no accumulated gate-check rows), every
// state transition must be eagerly checkpointed to disk so a crash is leave-diagnosable, and
// RetryStage must be able to force a fresh attempt from any stuck state so a user is never
// permanently blocked.
public class WorkflowResilienceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeGateRunner _gateRunner = new();
    private readonly FakeGitService _gitService = new();
    private readonly FakeGitCredentialStore _credentialStore = new();
    private readonly FakeBrokerCoordinator _coordinator = new();
    private readonly ActiveTurnTracker _turnTracker = new();

    public WorkflowResilienceTests()
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

    private WorkflowEngine CreateEngine() => new(
        CreateFactory(), _gateRunner, _coordinator, _broadcaster,
        _gitService, new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
        new ModelCatalogService(_coordinator), _credentialStore, _turnTracker);

    private static GateResult Pass(string evidence) => new(true, "OK", evidence);
    private static GateResult Fail(string evidence) => new(false, "failed", evidence);

    // scaffold_specs, core_scaffold, repo_hygiene, code_map, context_bundle (start-stage), then
    // gherkin FAIL, render (pass), then a re-run of gherkin+render PASS — the classic "gates
    // failed, artifact fixed, gates re-pass" BA story.
    private void SeedBaGateSequence_FailThenPass()
    {
        _gateRunner.Results.AddRange([
            Pass("scaffold"), Pass("core"), Pass("hygiene"), Pass("map"), Pass("context"),
            Fail("REQ-004 missing Given"), Pass("handoff"),
            Pass("gherkin fixed"), Pass("handoff"),
        ]);
    }

    private async Task<(WorkflowEngine Engine, Guid FeatureId, Guid RunId)> DriveBaToBlockedGateAsync()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var first = await engine.RunGatesAsync(featureId, CancellationToken.None);
        var run = first.StageRuns.Single(sr => sr.StageName == "business-analyst");
        Assert.Equal(ReleaseStageStatus.BlockedGate, run.Status);
        return (engine, featureId, run.Id);
    }

    private async Task SetRunStatusAsync(Guid featureId, string stageName, ReleaseStageStatus status)
    {
        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId && r.StageName == stageName);
        run.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<StageCheckpoint> GetCheckpointAsync(Guid featureId, string stageName)
    {
        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId && r.StageName == stageName);
        return StageCheckpoint.TryDeserialize(run.CheckpointJson)!;
    }

    // ─── re-entrant run-gates ─────────────────────────────────────────────

    [Fact]
    public async Task RunGates_OnBlockedGateRun_RerunsSameRunWithSameAttempt()
    {
        SeedBaGateSequence_FailThenPass();
        var (engine, featureId, runId) = await DriveBaToBlockedGateAsync();

        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

        var run = result.StageRuns.Single(sr => sr.Id == runId);
        Assert.Equal(ReleaseStageStatus.BlockedSignoff, run.Status);
        Assert.Equal(1, run.Attempt);
        // Exactly one human message was sent (DriveBaToBlockedGateAsync); the opening prompt
        // isn't itself a message.
        Assert.Equal(1, run.QuestionCount);
    }

    [Fact]
    public async Task RunGates_OnEscalatedRun_RerunsSameRun()
    {
        _gateRunner.Results.AddRange([Pass("scaffold"), Pass("core"), Pass("map"), Pass("context"), Pass("gherkin")]);
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var activeRun = (await engine.GetReleaseAsync(release.Id, CancellationToken.None))
            .StageRuns.Single(sr => sr.StageName == "business-analyst");
        await SetRunStatusAsync(featureId, "business-analyst", ReleaseStageStatus.Escalated);

        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

        var run = result.StageRuns.Single(sr => sr.Id == activeRun.Id);
        Assert.Equal(ReleaseStageStatus.BlockedSignoff, run.Status);
        Assert.Equal(1, run.Attempt);
    }

    [Fact]
    public async Task RunGates_WhenAnotherRunIsGatesRunning_ThrowsAlreadyRunning()
    {
        _gateRunner.Results.AddRange([Pass("scaffold"), Pass("core"), Pass("map"), Pass("context"), Pass("gherkin")]);
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await SetRunStatusAsync(featureId, "business-analyst", ReleaseStageStatus.GatesRunning);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunGatesAsync(featureId, CancellationToken.None));

        Assert.Contains("already running", ex.Message);
    }

    [Fact]
    public async Task RunGates_Rerun_ReplacesStaleExitChecksAndPreservesLeadingBuiltins()
    {
        SeedBaGateSequence_FailThenPass();
        var (engine, featureId, runId) = await DriveBaToBlockedGateAsync();

        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

        var run = result.StageRuns.Single(sr => sr.Id == runId);
        Assert.Equal(1, run.GateChecks.Count(gc => gc.Name == BuiltinRegistry.ScaffoldSpecs));
        Assert.Equal(1, run.GateChecks.Count(gc => gc.Name == BuiltinRegistry.CoreScaffold));
        Assert.Equal(1, run.GateChecks.Count(gc => gc.Name == BuiltinRegistry.ContextBundle));
        // The re-run sweeps the previous attempt's exit checks and records fresh ones, so each
        // exit check appears exactly once — never an accumulating history on the same run row.
        Assert.Equal(1, run.GateChecks.Count(gc => gc.Name == BuiltinRegistry.GherkinValidator));
        Assert.Equal(1, run.GateChecks.Count(gc => gc.Name == BuiltinRegistry.RenderHandoff));
        Assert.All(run.GateChecks, gc => Assert.True(gc.Passed));
    }

    // ─── eager on-disk checkpoints ─────────────────────────────────────────

    [Fact]
    public async Task StartStage_WritesPromptSucceededCheckpoint()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var checkpoint = await GetCheckpointAsync(featureId, "business-analyst");
        Assert.NotNull(checkpoint);
        Assert.Equal(StageCheckpointSignal.PromptSucceeded, checkpoint.Signal);
        Assert.Equal(StagePhase.GuidedQA, checkpoint.Phase);
    }

    [Fact]
    public async Task RunGates_WritesStepLedgerCheckpointIncludingFailures()
    {
        _gateRunner.Results.AddRange([Pass("scaffold"), Pass("core"), Pass("hygiene"), Pass("map"), Pass("context"), Fail("REQ-004 missing Given")]);
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        var checkpoint = await GetCheckpointAsync(featureId, "business-analyst");
        Assert.Equal(StageCheckpointSignal.Gates, checkpoint.Signal);
        Assert.Equal(StagePhase.Gates, checkpoint.Phase);
        Assert.NotNull(checkpoint.Steps);
        var gherkin = checkpoint.Steps.Single(s => s.Name == BuiltinRegistry.GherkinValidator);
        Assert.False(gherkin.Passed);
        Assert.Equal(2, checkpoint.Steps.Count);
    }

    // ─── RetryStage escape hatch ──────────────────────────────────────────

    [Fact]
    public async Task RetryStage_OnBlockedGate_SupersedesOldRunAndCreatesFreshActiveAttempt()
    {
        _gateRunner.Results.AddRange([Pass("scaffold"), Pass("core"), Pass("hygiene"), Pass("map"), Pass("context"), Fail("REQ-004 missing Given")]);
        var (engine, featureId, _) = await DriveBaToBlockedGateAsync();
        var promptsBefore = _coordinator.Prompts.Count;

        var result = await engine.RetryStageAsync(featureId, targetStageName: null, CancellationToken.None);

        var runs = result.StageRuns.Where(sr => sr.StageName == "business-analyst").ToList();
        Assert.Equal(2, runs.Count);
        var superseded = runs.Single(r => r.Status == ReleaseStageStatus.Stale);
        Assert.NotNull(superseded.FinishedAt);
        Assert.Contains("Superseded", superseded.Summary);
        var fresh = runs.Single(r => r.Status == ReleaseStageStatus.Active);
        Assert.Equal(2, fresh.Attempt);
        Assert.Equal(StagePhase.GuidedQA, fresh.Phase);
        Assert.Null(fresh.AcpSessionId);
        Assert.Equal(0, result.FlowPosition!.CurrentStageIndex);
        Assert.Equal(promptsBefore, _coordinator.Prompts.Count);
    }

    [Fact]
    public async Task RetryStage_OnGatesRunning_SupersedesAndCreatesFreshActiveAttempt()
    {
        _gateRunner.Results.AddRange([Pass("scaffold"), Pass("core"), Pass("map"), Pass("context"), Pass("gherkin")]);
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var activeRun = (await engine.GetReleaseAsync(release.Id, CancellationToken.None))
            .StageRuns.Single(sr => sr.StageName == "business-analyst");
        await SetRunStatusAsync(featureId, "business-analyst", ReleaseStageStatus.GatesRunning);

        var result = await engine.RetryStageAsync(featureId, targetStageName: null, CancellationToken.None);

        var runs = result.StageRuns.Where(sr => sr.StageName == "business-analyst").ToList();
        Assert.Single(runs, r => r.Id == activeRun.Id && r.Status == ReleaseStageStatus.Stale);
        var fresh = runs.Single(r => r.Id != activeRun.Id);
        Assert.Equal(ReleaseStageStatus.Active, fresh.Status);
        Assert.Equal(2, fresh.Attempt);
        Assert.Equal(0, result.FlowPosition!.CurrentStageIndex);
    }

    [Fact]
    public async Task RetryStage_TargetingEarlierStage_RewindsPositionAndClearsItsSignoff()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var gated = await engine.RunGatesAsync(featureId, CancellationToken.None);
        Assert.Equal(ReleaseStageStatus.BlockedSignoff,
            gated.StageRuns.Single(sr => sr.StageName == "business-analyst").Status);
        var afterSignoff = await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);
        Assert.Equal(1, afterSignoff.FlowPosition!.CurrentStageIndex);
        // A signoff row is keyed by the stage it gates, not by the signoff id.
        Assert.True(afterSignoff.Signoffs.Single(s => s.StageName == "business-analyst").Approved);

        var result = await engine.RetryStageAsync(featureId, targetStageName: "business-analyst", CancellationToken.None);

        Assert.Equal(0, result.FlowPosition!.CurrentStageIndex);
        Assert.Equal("business-analyst", result.FlowPosition.CurrentStageName);
        Assert.False(result.Signoffs.Single(s => s.StageName == "business-analyst").Approved);
        var runs = result.StageRuns.Where(sr => sr.StageName == "business-analyst").ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Status == ReleaseStageStatus.Stale);
        var fresh = runs.Single(r => r.Status == ReleaseStageStatus.Active);
        Assert.Equal(2, fresh.Attempt);
        Assert.Equal(ReleaseStatus.InProgress, result.Status);
    }

    [Fact]
    public async Task RetryStage_WhenAllStagesDone_RewindsToLastStage()
    {
        var pipeline = new WorkflowDefinitionLoader().LoadDefault();
        var lastRole = pipeline.Pipeline[^1];
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await using (var db = CreateFactory().CreateDbContext())
        {
            var feature = db.ReleaseFeatures.Include(f => f.FlowPosition).Single(f => f.Id == featureId);
            feature.FlowPosition!.CurrentStageIndex = pipeline.Pipeline.Count;
            feature.FlowPosition.CurrentStageName = "done";
            // Add through the DbSet, not the navigation: a fresh run with a non-default Guid key
            // discovered via a navigation reads as Modified and trips the concurrency guard
            // (see WorkflowEngine.RetryStageAsync).
            db.ReleaseStageRuns.Add(new ReleaseStageRun
            {
                ReleaseFeatureId = featureId,
                StageName = lastRole.Name,
                Status = ReleaseStageStatus.Complete,
                Phase = StagePhase.Signoff,
                Attempt = 1,
                ReadyToProceed = true,
            });
            await db.SaveChangesAsync();
        }

        var result = await engine.RetryStageAsync(featureId, targetStageName: null, CancellationToken.None);

        Assert.Equal(pipeline.Pipeline.Count - 1, result.FlowPosition!.CurrentStageIndex);
        Assert.Equal(lastRole.Name, result.FlowPosition.CurrentStageName);
        var runs = result.StageRuns.Where(sr => sr.StageName == lastRole.Name).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Status == ReleaseStageStatus.Stale);
        var fresh = runs.Single(r => r.Status == ReleaseStageStatus.Active);
        Assert.Equal(2, fresh.Attempt);
    }

    [Fact]
    public async Task RetryStage_UnknownTargetStageName_Throws()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RetryStageAsync(featureId, targetStageName: "does-not-exist", CancellationToken.None));

        Assert.Contains("does-not-exist", ex.Message);
    }
}