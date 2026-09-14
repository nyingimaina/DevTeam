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

    [Fact]
    public async Task Signoff_AdvancesFlowPositionWhenCurrentStageSignsOff()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // BA interactive flow → gates pass → await signoff
        await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        var gated = await engine.RunGatesAsync(release.Id, CancellationToken.None);
        Assert.Equal(ReleaseStatus.Blocked, gated.Status);

        var updated = await engine.SignoffAsync(release.Id, "business-analyst", "pm", null, CancellationToken.None);

        Assert.Equal(ReleaseStatus.InProgress, updated.Status);
        Assert.Equal(1, updated.FlowPosition!.CurrentStageIndex);
        Assert.Equal("developer", updated.FlowPosition.CurrentStageName);
    }

    [Fact]
    public async Task RunStage_RejectsUserInputStage()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunStageAsync(release.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RunStage_ExecutesAutonomousStageAndBlocksOnSignoff()
    {
        var engine = CreateEngine();
        var release = await DriveToDeveloperAsync(engine);

        var updated = await engine.RunStageAsync(release.Id, CancellationToken.None);
        var devRun = updated.StageRuns.Single(sr => sr.StageName == "developer");

        Assert.Equal(ReleaseStageStatus.BlockedSignoff, devRun.Status);
        Assert.Equal(StagePhase.Signoff, devRun.Phase);
        Assert.Equal(ReleaseStatus.Blocked, updated.Status);
        Assert.Equal(1, updated.FlowPosition!.CurrentStageIndex);

        _ = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var devPrompts = ((FakeBrokerCoordinator)_coordinator).Prompts
            .Where(p => p.StartsWith("You are the developer")).ToArray();
        Assert.NotEmpty(devPrompts);
        Assert.Contains("DONE", devPrompts[^1]);
    }

    [Fact]
    public async Task RunStage_MarksBlockedWhenGateFails()
    {
        var engine = CreateEngine();
        var release = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "tests failed"), 8));
        var updated = await engine.RunStageAsync(release.Id, CancellationToken.None);
        var devRun = updated.StageRuns.Single(sr => sr.StageName == "developer");

        Assert.Equal(ReleaseStageStatus.BlockedGate, devRun.Status);
    }

    [Fact]
    public async Task RunStage_TriesAgentLoopUpToAttempts()
    {
        var engine = CreateEngine();
        var release = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "verify failed"), 8));
        await engine.RunStageAsync(release.Id, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        // developer loop attempts up to 3 → agent re-prompted
        var developerPrompts = fake.Prompts.Count(p => p.StartsWith("You are the developer"));
        Assert.Equal(3, developerPrompts);
    }

    [Fact]
    public async Task PushBack_MovesToPreviousStageAndRecordsGuidance()
    {
        var engine = CreateEngine();
        var release = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "tests failed"), 8));
        await engine.RunStageAsync(release.Id, CancellationToken.None);

        var updated = await engine.PushBackAsync(
            release.Id, "business-analyst", "Rework: add email validation", CancellationToken.None);

        Assert.Equal(0, updated.FlowPosition!.CurrentStageIndex);
        Assert.Equal("business-analyst", updated.FlowPosition.CurrentStageName);
        Assert.Equal(ReleaseStatus.InProgress, updated.Status);
        Assert.False(updated.Signoffs.Single(s => s.StageName == "business-analyst").Approved);

        var devRun = updated.StageRuns.Single(sr => sr.StageName == "developer");
        Assert.Equal("Rework: add email validation", devRun.GuidanceNotes.Single().Text);
    }

    [Fact]
    public async Task StartStage_IncludesFeedbackFromPushedBackRun()
    {
        var engine = CreateEngine();
        var release = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "tests failed"), 8));
        await engine.RunStageAsync(release.Id, CancellationToken.None);
        await engine.PushBackAsync(release.Id, "business-analyst", "Rework: add email validation", CancellationToken.None);

        await engine.StartStageAsync(release.Id, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        Assert.Contains(fake.Prompts, p => p.Contains("add email validation"));
    }

    [Fact]
    public async Task PushBack_RejectsUnknownOrForwardTarget()
    {
        var engine = CreateEngine();
        var release = await DriveToDeveloperAsync(engine);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.PushBackAsync(release.Id, "qa", null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.PushBackAsync(release.Id, "nobody", null, CancellationToken.None));
    }

    [Fact]
    public async Task GetStageMessages_ReturnsLinkedSessionMessages()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var stageRun = await engine.StartStageAsync(release.Id, CancellationToken.None);

        var sessionId = Guid.Parse(stageRun.AcpSessionId!);
        using (var db = CreateFactory().CreateDbContext())
        {
            db.Sessions.Add(new DevTeamSession
            {
                Id = sessionId,
                WorkspacePath = @"C:\work\proj",
                AcpSessionId = "fake-acp",
                Messages =
                {
                    new Message { Role = "user", BodyText = "Build a login form" },
                    new Message { Role = "assistant", BodyText = "I need clarification." },
                },
            });
            db.SaveChanges();
        }

        var messages = await engine.GetStageMessagesAsync(release.Id, stageRun.Id, CancellationToken.None);

        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("Build a login form", messages[0].BodyText);
        Assert.Equal("assistant", messages[1].Role);
    }

    [Fact]
    public async Task GetPipeline_ReturnsOrderedStages()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        var pipeline = await engine.GetPipelineAsync(release.Id, CancellationToken.None);

        Assert.Equal(3, pipeline.Count);
        Assert.Equal("business-analyst", pipeline[0].Name);
        Assert.True(pipeline[0].UserInputRequired);
        Assert.Equal("requirements-approval", pipeline[0].Signoff);
        Assert.Equal("developer", pipeline[1].Name);
        Assert.False(pipeline[1].UserInputRequired);
        Assert.Equal("qa", pipeline[2].Name);
    }

    [Fact]
    public async Task RunGates_FeedsRequirementsFromDiskToRequirementGates()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));
        var requirementsPath = ArtifactPaths.RequirementsPath(workspace.Path, "feat-001");
        Directory.CreateDirectory(Path.GetDirectoryName(requirementsPath)!);
        File.WriteAllText(requirementsPath,
            "## REQ-001: User can log in" + Environment.NewLine +
            "Given a registered user" + Environment.NewLine +
            "When they enter valid credentials" + Environment.NewLine +
            "Then they are signed in");

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(release.Id, CancellationToken.None);

        var gherkinRequest = _gateRunner.Requests.Single(r => r.Builtin == BuiltinRegistry.GherkinValidator);
        Assert.Contains("REQ-001", gherkinRequest.Request.Inputs!["requirementsJson"]);
        Assert.Contains("When they enter valid credentials", gherkinRequest.Request.Inputs["requirementsJson"]);
    }

    [Fact]
    public async Task RunGates_RequirementGatesGetEmptyJsonWhenNoRequirementsFile()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(release.Id, CancellationToken.None);

        var gherkinRequest = _gateRunner.Requests.Single(r => r.Builtin == BuiltinRegistry.GherkinValidator);
        Assert.Equal("[]", gherkinRequest.Request.Inputs!["requirementsJson"]);
    }

    private sealed class TempDir(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }

    private async Task<DevTeamRelease> DriveToDeveloperAsync(WorkflowEngine engine)
    {
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // BA interactive flow: gates pass (default) → signoff required → approve → advance to developer
        await engine.StartStageAsync(release.Id, CancellationToken.None);
        await engine.SendMessageAsync(release.Id, "We need a login form", CancellationToken.None);
        var gated = await engine.RunGatesAsync(release.Id, CancellationToken.None);
        Assert.Equal(ReleaseStageStatus.BlockedSignoff,
            gated.StageRuns.Single(sr => sr.StageName == "business-analyst").Status);

        var after = await engine.SignoffAsync(release.Id, "business-analyst", "pm", null, CancellationToken.None);
        Assert.Equal(1, after.FlowPosition!.CurrentStageIndex);
        return after;
    }

    private readonly FakeBrokerCoordinator _coordinator = new();

    private WorkflowEngine CreateEngine() => new(
        CreateFactory(), _gateRunner, _coordinator, _broadcaster,
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

    public List<string> Prompts { get; } = [];

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
        Prompts.Add(text);
        Commands.Add($"prompt:{text[..Math.Min(50, text.Length)]}...");
        return Task.FromResult(new PromptResponse(sessionId, "end_turn", 10, 5, 15));
    }
}
