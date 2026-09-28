using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

/// <summary>
/// The 'verification' stage is the only pipeline stage with no agent and no signoff — it exists
/// purely to run the strict final checks before a feature is allowed to complete. These tests
/// pin that behaviour: it runs, it opens no model session, a failure blocks completion, and a
/// pass lets the feature complete and the release become Ready.
/// </summary>
public class DeterministicStageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeGitService _gitService = new();
    private readonly FakeGitCredentialStore _credentialStore = new();
    private readonly FakeBrokerCoordinator _coordinator = new();
    private readonly ActiveTurnTracker _turnTracker = new();

    public DeterministicStageTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task VerificationStage_RunsFinalChecks_WithoutOpeningAModelSession()
    {
        var gateRunner = new FakeGateRunner();
        var engine = CreateEngine(gateRunner);
        var (release, featureId) = await DriveToVerificationAsync(engine);

        var sessionsBefore = _coordinator.NewSessionCallCount;
        var result = await engine.RunStageAsync(featureId, CancellationToken.None);

        // The deterministic stage ran its builtin…
        Assert.Contains(gateRunner.Requests, r => r.Builtin == BuiltinRegistry.FinalChecks);
        // …without starting a model session, and the feature is now done.
        Assert.Equal(sessionsBefore, _coordinator.NewSessionCallCount);
        Assert.Equal(ReleaseFeatureStatus.Complete, result.Features.Single().Status);
        Assert.Equal(ReleaseStatus.Ready, result.Status);
    }

    [Fact]
    public async Task VerificationStage_KeepsTheFeatureOpen_WhenAFinalCheckFails()
    {
        var engine = CreateEngine(new FailFinalChecksGateRunner());
        var (_, featureId) = await DriveToVerificationAsync(engine);

        var result = await engine.RunStageAsync(featureId, CancellationToken.None);

        var stageRun = result.StageRuns.Single(sr => sr.StageName == "verification");
        Assert.Equal(ReleaseStageStatus.BlockedGate, stageRun.Status);
        Assert.NotEqual(ReleaseFeatureStatus.Complete, result.Features.Single().Status);
        Assert.Equal("verification", result.FlowPosition!.CurrentStageName);
    }

    private async Task<(DevTeamRelease Release, Guid FeatureId)> DriveToVerificationAsync(WorkflowEngine engine)
    {
        var release = await engine.StartReleaseWithFeatureAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        await engine.RunStageAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "developer", "pm", null, CancellationToken.None);

        var atQa = await engine.RunStageAsync(featureId, CancellationToken.None);
        Assert.Equal("qa", atQa.FlowPosition!.CurrentStageName);

        var atVerification = await engine.SignoffAsync(featureId, "qa", "pm", null, CancellationToken.None);
        Assert.Equal("verification", atVerification.FlowPosition!.CurrentStageName);
        return (atVerification, featureId);
    }

    private WorkflowEngine CreateEngine(IGateRunner gateRunner) => new(
        CreateFactory(), gateRunner, _coordinator, _broadcaster,
        _gitService, new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
        new ModelCatalogService(_coordinator), _credentialStore, _turnTracker);

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    private sealed class FailFinalChecksGateRunner : IGateRunner
    {
        public Task<GateResult> RunAsync(string builtin, GateRequest request, CancellationToken cancellationToken)
            => Task.FromResult(builtin == BuiltinRegistry.FinalChecks
                ? GateResult.Fail("Some final checks didn't pass.", "- The screen tests pass: 3 failed")
                : GateResult.Pass("OK", string.Empty));
    }
}
