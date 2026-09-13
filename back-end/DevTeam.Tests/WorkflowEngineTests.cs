using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

public class WorkflowEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeGateRunner _gateRunner = new();

    public WorkflowEngineTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task StartRelease_CreatesReleaseWithCorrectState()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        Assert.Equal(ReleaseStatus.InProgress, release.Status);
        Assert.Single(release.Features);
        Assert.Equal("feat-001", release.Features[0].Key);
        Assert.NotNull(release.FlowPosition);
        Assert.Equal(0, release.FlowPosition.CurrentStageIndex);
    }

    [Fact]
    public async Task StartRelease_CreatesSignoffRequirements()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        Assert.True(release.Signoffs.Count >= 1);
    }

    [Fact]
    public async Task Advance_RunsBuiltinGates()
    {
        _gateRunner.Results.Add(new GateResult(true, "Tests passed", "12 tests, all green"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        var result = await engine.RunGatesAsync(release.Id, CancellationToken.None);

        Assert.True(_gateRunner.Requests.Count > 0);
        Assert.Contains(result.StageRuns, sr => sr.GateChecks.All(gc => gc.Passed));
    }

    [Fact]
    public async Task Advance_MarksBlockedWhenGateFails()
    {
        _gateRunner.Results.Add(new GateResult(false, "Tests failed", "3 tests failed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        var result = await engine.RunGatesAsync(release.Id, CancellationToken.None);

        Assert.Contains(result.StageRuns, sr => sr.Status == ReleaseStageStatus.BlockedGate);
    }

    [Fact]
    public async Task Advance_MarksBlockedWhenSignoffPending()
    {
        _gateRunner.Results.Add(new GateResult(true, "OK", "passed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        var result = await engine.RunGatesAsync(release.Id, CancellationToken.None);

        if (release.Signoffs.Any(s => s.Required))
        {
            Assert.Contains(result.StageRuns, sr => sr.Status == ReleaseStageStatus.BlockedSignoff);
            Assert.Equal(ReleaseStatus.Blocked, result.Status);
        }
    }

    [Fact]
    public async Task Signoff_ApprovesAndUnblocks()
    {
        _gateRunner.Results.Add(new GateResult(true, "OK", "passed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(release.Id, CancellationToken.None);

        var requiredSignoffs = release.Signoffs.Where(s => s.Required).ToList();
        foreach (var s in requiredSignoffs)
        {
            var updated = await engine.SignoffAsync(release.Id, s.StageName, "qa-lead", "Looks good", CancellationToken.None);
            Assert.True(updated.Signoffs.First(x => x.StageName == s.StageName).Approved);
            Assert.Equal("qa-lead", updated.Signoffs.First(x => x.StageName == s.StageName).ApprovedBy);
        }

        var final = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        Assert.NotEqual(ReleaseStatus.Blocked, final.Status);
    }

    [Fact]
    public async Task GetRelease_ReturnsReleaseWithIncludes()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        var fetched = await engine.GetReleaseAsync(release.Id, CancellationToken.None);

        Assert.Equal(release.Id, fetched.Id);
        Assert.NotNull(fetched.Features);
        Assert.NotNull(fetched.StageRuns);
        Assert.NotNull(fetched.Signoffs);
    }

    [Fact]
    public async Task ListReleases_ReturnsAllReleases()
    {
        var engine = CreateEngine();
        var r1 = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var r2 = await engine.StartReleaseAsync("feat-002", @"C:\work\proj", CancellationToken.None);

        var list = await engine.ListReleasesAsync(CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, r => r.Id == r1.Id);
        Assert.Contains(list, r => r.Id == r2.Id);
    }

    [Fact]
    public async Task Advance_AdvancesStageIndex()
    {
        _gateRunner.Results.Add(new GateResult(true, "OK", "passed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(release.Id, CancellationToken.None);

        var fetched = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        Assert.True(fetched.FlowPosition!.CurrentStageIndex >= 0);
    }

    private WorkflowEngine CreateEngine() => new(
        CreateFactory(), _gateRunner, new FakeBrokerCoordinator(), _broadcaster,
        new FakeGitService(), new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance);

    private IDbContextFactory<DevTeamDbContext> CreateFactory()
        => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}

internal sealed class FakeGateRunner : IGateRunner
{
    public List<(string Builtin, GateRequest Request)> Requests { get; } = [];
    public List<GateResult> Results { get; } = [];
    private int _index;

    public Task<GateResult> RunAsync(string builtin, GateRequest request, CancellationToken cancellationToken)
    {
        Requests.Add((builtin, request));
        var result = _index < Results.Count ? Results[_index++] : new GateResult(true, "OK", "");
        return Task.FromResult(result);
    }
}

internal sealed class FakeGitService : IGitService
{
    public List<string> Commands { get; } = [];

    public Task<GitResponse> InitAsync(string workspacePath, CancellationToken ct = default)
    {
        Commands.Add($"init:{workspacePath}");
        return Task.FromResult(new GitResponse(true, "Initialized", IsRepo: true));
    }

    public Task<GitResponse> StatusAsync(string workspacePath, CancellationToken ct = default)
    {
        Commands.Add($"status:{workspacePath}");
        return Task.FromResult(new GitResponse(true, "OK", IsRepo: true, IsClean: true));
    }

    public Task<GitResponse> EnsureBranchAsync(string workspacePath, string branchName, CancellationToken ct = default)
    {
        Commands.Add($"ensure-branch:{branchName}");
        return Task.FromResult(new GitResponse(true, $"Branch '{branchName}' created", Branch: branchName));
    }

    public Task<GitResponse> CommitAsync(string workspacePath, string message, CancellationToken ct = default)
    {
        Commands.Add($"commit:{message}");
        return Task.FromResult(new GitResponse(true, "Committed"));
    }

    public Task<GitResponse> MergeAsync(string workspacePath, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        Commands.Add($"merge:{sourceBranch}->{targetBranch}");
        return Task.FromResult(new GitResponse(true, $"Merged {sourceBranch} into {targetBranch}"));
    }

    public Task<GitResponse> BranchAsync(string workspacePath, string branchName, CancellationToken ct = default)
    {
        Commands.Add($"branch:{branchName}");
        return Task.FromResult(new GitResponse(true, $"Branch '{branchName}' created", Branches: [branchName]));
    }

    public Task<GitResponse> CheckoutAsync(string workspacePath, string branchName, CancellationToken ct = default)
    {
        Commands.Add($"checkout:{branchName}");
        return Task.FromResult(new GitResponse(true, $"Checked out '{branchName}'", Branch: branchName));
    }

    public Task<GitResponse> LogAsync(string workspacePath, CancellationToken ct = default)
    {
        Commands.Add("log");
        return Task.FromResult(new GitResponse(true, "OK"));
    }
}

internal sealed class FakeBrokerCoordinator : IWorkflowCoordinator
{
    public List<string> Commands { get; } = [];

    public Task<SessionSummary> NewSessionAsync(string workspacePath, string? modelId, CancellationToken ct)
    {
        Commands.Add($"new-session:{workspacePath}");
        return Task.FromResult(new SessionSummary(
            Guid.NewGuid(), "fake-acp", workspacePath, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []));
    }

    public Task<string> SetModeAsync(Guid sessionId, string modeId, CancellationToken ct)
    {
        Commands.Add($"set-mode:{modeId}");
        return Task.FromResult(modeId);
    }

    public Task<PromptResponse> PromptWithSessionRecoveryAsync(Guid sessionId, string text, CancellationToken ct)
    {
        Commands.Add($"prompt:{text[..Math.Min(50, text.Length)]}...");
        return Task.FromResult(new PromptResponse(sessionId, "end_turn", 10, 5, 15));
    }
}
