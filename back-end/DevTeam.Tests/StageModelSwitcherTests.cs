using DevTeam.Broker.Domain;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

public class StageModelSwitcherTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FakeGateRunner _gateRunner = new();
    private readonly FakeBrokerCoordinator _coordinator = new();
    private readonly FakeModelSwitchBackend _backend = new();

    public StageModelSwitcherTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new Factory(_connection);

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    private sealed class FakeModelSwitchBackend : IModelSwitchBackend
    {
        public List<(Guid SessionId, string ModelId)> SetModelCalls { get; } = [];
        public List<(Guid SessionId, string Text, bool IsPriming)> Probes { get; } = [];
        public Exception? SetModelThrows { get; set; }
        public Exception? ProbeThrows { get; set; }
        public bool ProbeHangs { get; set; }

        public Task SetModelAsync(Guid sessionId, string modelId, CancellationToken ct)
        {
            SetModelCalls.Add((sessionId, modelId));
            if (SetModelThrows is not null) throw SetModelThrows;
            return Task.CompletedTask;
        }

        public async Task ProbeAsync(Guid sessionId, string text, bool isPriming, CancellationToken ct)
        {
            Probes.Add((sessionId, text, isPriming));
            if (ProbeHangs) await Task.Delay(Timeout.Infinite, ct);
            if (ProbeThrows is not null) throw ProbeThrows;
        }
    }

    private StageModelSwitcher CreateSwitcher(TimeSpan? probeTimeout = null)
        => new(CreateFactory(), _backend, NullLogger<StageModelSwitcher>.Instance, probeTimeout ?? TimeSpan.FromSeconds(5));

    // A feature whose current stage's last agent turn was refused by the provider.
    private async Task<(Guid FeatureId, Guid SessionId)> SeedEscalatedStageAsync()
    {
        var engine = new WorkflowEngine(
            CreateFactory(), _gateRunner, _coordinator, new RecordingBroadcaster(),
            new FakeGitService(), new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
            new ModelCatalogService(_coordinator), new FakeGitCredentialStore(), new ActiveTurnTracker());
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        await using var db = CreateFactory().CreateDbContext();
        var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId);
        run.Status = ReleaseStageStatus.Escalated;
        run.LastErrorKind = StageErrorKind.ProviderRejected;
        run.LastErrorMessage = "Error from provider (Console): OpenCode's free tier can only be used from within OpenCode";
        run.LastErrorAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return (featureId, Guid.Parse(run.AcpSessionId!));
    }

    private ReleaseStageRun LoadRun(Guid featureId)
    {
        using var db = CreateFactory().CreateDbContext();
        return db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId);
    }

    [Fact]
    public async Task Success_SwitchesTheModelSendsAConnectionCheckAndClearsTheFailure()
    {
        var (featureId, sessionId) = await SeedEscalatedStageAsync();

        var result = await CreateSwitcher().SwitchAndVerifyAsync(featureId, "google/gemini-3.6-flash", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("google/gemini-3.6-flash", result.ModelId);
        Assert.Equal([(sessionId, "google/gemini-3.6-flash")], _backend.SetModelCalls);
        var probe = Assert.Single(_backend.Probes);
        Assert.Equal(sessionId, probe.SessionId);
        Assert.True(probe.IsPriming); // a housekeeping message, not part of the user's conversation

        var run = LoadRun(featureId);
        Assert.Equal(ReleaseStageStatus.Active, run.Status);
        Assert.Equal(StageErrorKind.None, run.LastErrorKind);
        Assert.Null(run.LastErrorMessage);
    }

    [Fact]
    public async Task ModelCannotBeSelected_ReportsFailureAndLeavesTheStageEscalated()
    {
        var (featureId, _) = await SeedEscalatedStageAsync();
        _backend.SetModelThrows = new RpcException("Unknown model 'nope'", -32602);

        var result = await CreateSwitcher().SwitchAndVerifyAsync(featureId, "nope", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("try another", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_backend.Probes); // no point checking a connection to a model we couldn't select
        var run = LoadRun(featureId);
        Assert.Equal(ReleaseStageStatus.Escalated, run.Status);
        Assert.Equal(StageErrorKind.ProviderRejected, run.LastErrorKind);
    }

    [Fact]
    public async Task ProviderRefusesTheConnectionCheck_ReportsFailureWithTheProvidersWords()
    {
        var (featureId, _) = await SeedEscalatedStageAsync();
        _backend.ProbeThrows = new RpcException("Error from provider (Console): quota exceeded", -32603);

        var result = await CreateSwitcher().SwitchAndVerifyAsync(featureId, "some/model", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("quota exceeded", result.Message);
        Assert.Contains("try another", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReleaseStageStatus.Escalated, LoadRun(featureId).Status);
    }

    [Fact]
    public async Task AgentDropsDuringTheCheck_ReportsFailure()
    {
        var (featureId, _) = await SeedEscalatedStageAsync();
        _backend.ProbeThrows = new AcpDisconnectedException("pipe closed");

        var result = await CreateSwitcher().SwitchAndVerifyAsync(featureId, "some/model", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("stopped", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReleaseStageStatus.Escalated, LoadRun(featureId).Status);
    }

    [Fact]
    public async Task NoAnswerInTime_ReportsFailureInsteadOfHanging()
    {
        var (featureId, _) = await SeedEscalatedStageAsync();
        _backend.ProbeHangs = true;

        var result = await CreateSwitcher(TimeSpan.FromMilliseconds(150))
            .SwitchAndVerifyAsync(featureId, "some/model", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("in time", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReleaseStageStatus.Escalated, LoadRun(featureId).Status);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var (featureId, _) = await SeedEscalatedStageAsync();
        _backend.ProbeHangs = true;
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSwitcher().SwitchAndVerifyAsync(featureId, "some/model", cts.Token));
    }

    [Fact]
    public async Task FailureMessages_NeverLeakInternalTypeNames()
    {
        var (featureId, _) = await SeedEscalatedStageAsync();
        _backend.ProbeThrows = new RpcException("boom", -32603);

        var result = await CreateSwitcher().SwitchAndVerifyAsync(featureId, "some/model", CancellationToken.None);

        Assert.DoesNotContain("RpcException", result.Message);
        Assert.DoesNotContain("ACP", result.Message);
        Assert.DoesNotContain("   at ", result.Message);
    }

    [Fact]
    public async Task NoStageToResume_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSwitcher().SwitchAndVerifyAsync(Guid.NewGuid(), "some/model", CancellationToken.None));
    }

    [Fact]
    public async Task StageThatIsNotFailed_StillSwitchesButDoesNotTouchItsState()
    {
        var (featureId, _) = await SeedEscalatedStageAsync();
        await using (var db = CreateFactory().CreateDbContext())
        {
            var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId);
            run.Status = ReleaseStageStatus.Active;
            run.LastErrorKind = StageErrorKind.None;
            run.LastErrorMessage = null;
            await db.SaveChangesAsync();
        }

        var result = await CreateSwitcher().SwitchAndVerifyAsync(featureId, "some/model", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(ReleaseStageStatus.Active, LoadRun(featureId).Status);
    }
}
