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

public class WorkflowEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeGateRunner _gateRunner = new();
    private readonly FakeGitService _gitService = new();
    private readonly FakeGitCredentialStore _credentialStore = new();

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
        Assert.Equal("release/feat-001", release.BranchName);
        Assert.Equal("feature/feat-001", release.Features[0].BranchName);
        Assert.Equal(release.Features[0].Id, release.CurrentFeatureId);
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
    public async Task StartStage_PromptTellsAgentHandoffIsAutomatic()
    {
        // Regression: without this, a role's agent has no self-knowledge of DevTeam's
        // orchestration and — when asked how to proceed — hallucinates that the next
        // role's agent/config is missing instead of trusting the automatic handoff.
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var openingPrompt = fake.Prompts.Single(p => p.StartsWith("You are the business-analyst"));
        Assert.Contains("DevTeam automatically starts the next role's agent session", openingPrompt);
        Assert.Contains("must never tell the user their opencode config or repo is missing a role or agent", openingPrompt);
    }

    [Fact]
    public async Task RunStage_PromptTellsAgentNooneIsWatchingSoStayTerse()
    {
        // Autonomous stages (developer/qa) have no one watching live — the opening prompt
        // should discourage step-by-step narration so tokens aren't spent on commentary
        // nobody reads. Interactive stages (business-analyst) keep a human in the loop live,
        // so that prompt is intentionally unaffected.
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);
        await engine.RunStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var developerPrompt = fake.Prompts.First(p => p.StartsWith("You are the developer"));
        Assert.Contains("no one is watching this stage", developerPrompt);
        Assert.Contains("keep output terse", developerPrompt);

        var baPrompt = fake.Prompts.Single(p => p.StartsWith("You are the business-analyst"));
        Assert.DoesNotContain("no one is watching this stage", baPrompt);
    }

    [Fact]
    public async Task StartStage_IncludesTheActiveDefaultProfilesPromptForThatStage()
    {
        await using (var seedDb = CreateFactory().CreateDbContext())
        {
            var profile = new Profile { Name = "Terse", Description = "Short answers", IsDefault = true };
            profile.Prompts.Add(new ProfilePrompt { StageName = "business-analyst", PromptText = "Keep it brief and to the point." });
            seedDb.Profiles.Add(profile);
            await seedDb.SaveChangesAsync(CancellationToken.None);
        }

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var openingPrompt = fake.Prompts.Single(p => p.StartsWith("You are the business-analyst"));
        Assert.Contains("Keep it brief and to the point.", openingPrompt);
    }

    [Fact]
    public async Task StartStage_OmitsProfileClauseWhenNoProfilesExist()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var openingPrompt = fake.Prompts.Single(p => p.StartsWith("You are the business-analyst"));
        Assert.DoesNotContain("active profile", openingPrompt);
    }

    [Fact]
    public async Task RunStage_UsesTheWorkspacesOwnProfileOverTheGlobalDefault()
    {
        var workspacePath = @"C:\work\proj-profile-override";
        Guid workspaceProfileId;
        await using (var seedDb = CreateFactory().CreateDbContext())
        {
            var defaultProfile = new Profile { Name = "Default", IsDefault = true };
            defaultProfile.Prompts.Add(new ProfilePrompt { StageName = "developer", PromptText = "DEFAULT_PROFILE_TEXT" });

            var workspaceProfile = new Profile { Name = "ForThisWorkspace" };
            workspaceProfile.Prompts.Add(new ProfilePrompt { StageName = "developer", PromptText = "WORKSPACE_PROFILE_TEXT" });

            seedDb.Profiles.AddRange(defaultProfile, workspaceProfile);
            await seedDb.SaveChangesAsync(CancellationToken.None);
            workspaceProfileId = workspaceProfile.Id;

            seedDb.WorkspaceProfileSettings.Add(new WorkspaceProfileSettings { WorkspacePath = workspacePath, ProfileId = workspaceProfileId });
            await seedDb.SaveChangesAsync(CancellationToken.None);
        }

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspacePath, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        var afterSignoff = await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);
        Assert.Equal(1, afterSignoff.FlowPosition!.CurrentStageIndex);

        await engine.RunStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var developerPrompt = fake.Prompts.First(p => p.StartsWith("You are the developer"));
        Assert.Contains("WORKSPACE_PROFILE_TEXT", developerPrompt);
        Assert.DoesNotContain("DEFAULT_PROFILE_TEXT", developerPrompt);
    }

    [Fact]
    public async Task StartStage_OverrideModeReplacesTheEntireBuiltInPromptWithTheProfileText()
    {
        // Explicit, user-accepted tradeoff: override mode sends ONLY the profile's text —
        // not the framework framing, not HandoffAutomationClause, not BRS-authoring
        // instructions. Asserted with Equal (not Contains) so a regression that leaks any
        // framework text back in is caught.
        await using (var seedDb = CreateFactory().CreateDbContext())
        {
            var profile = new Profile { Name = "FullOverride", IsDefault = true };
            profile.Prompts.Add(new ProfilePrompt
            {
                StageName = "business-analyst",
                PromptText = "OVERRIDE_TEXT_ONLY",
                OverridesBuiltInPrompt = true,
            });
            seedDb.Profiles.Add(profile);
            await seedDb.SaveChangesAsync(CancellationToken.None);
        }

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var openingPrompt = Assert.Single(fake.Prompts);
        Assert.Equal("OVERRIDE_TEXT_ONLY", openingPrompt);
    }

    [Fact]
    public async Task StartStage_AugmentModeStillAppendsAfterTheBuiltInPromptWhenOverrideIsOff()
    {
        // Sibling of the override test above: default (OverridesBuiltInPrompt = false)
        // behavior is unchanged — the profile text is appended, framework text stays.
        await using (var seedDb = CreateFactory().CreateDbContext())
        {
            var profile = new Profile { Name = "Augment", IsDefault = true };
            profile.Prompts.Add(new ProfilePrompt
            {
                StageName = "business-analyst",
                PromptText = "AUGMENT_TEXT_ONLY",
                OverridesBuiltInPrompt = false,
            });
            seedDb.Profiles.Add(profile);
            await seedDb.SaveChangesAsync(CancellationToken.None);
        }

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var openingPrompt = fake.Prompts.Single(p => p.StartsWith("You are the business-analyst"));
        Assert.Contains("AUGMENT_TEXT_ONLY", openingPrompt);
        Assert.Contains("DevTeam automatically starts the next role's agent session", openingPrompt);
    }

    [Fact]
    public async Task StartStage_MarksTheOpeningPromptAsPriming_ButNotAGenuineUserReply()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        Assert.True(fake.PromptIsPriming[0]);

        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        Assert.False(fake.PromptIsPriming[^1]);
    }

    [Fact]
    public async Task SendMessage_WhenTheAgentProcessDisconnects_ClassifiesAndEscalates()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        fake.ExceptionsToThrow.Enqueue(new AcpDisconnectedException("ACP process exited"));

        await Assert.ThrowsAsync<AcpDisconnectedException>(
            () => engine.SendMessageAsync(featureId, "hello?", CancellationToken.None));

        var updated = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        var baRun = updated.Features[0].StageRuns.Single(sr => sr.StageName == "business-analyst");
        Assert.Equal(StageErrorKind.Disconnected, baRun.LastErrorKind);
        Assert.Equal(ReleaseStageStatus.Escalated, baRun.Status);
        Assert.NotNull(baRun.LastErrorAt);
    }

    [Fact]
    public async Task SendMessage_WhenTheProviderRejectsTheRequest_ClassifiesAsProviderRejectedNotDisconnected()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        fake.ExceptionsToThrow.Enqueue(new RpcException("Upstream request failed: invalid_request_error", -32603));

        await Assert.ThrowsAsync<RpcException>(
            () => engine.SendMessageAsync(featureId, "hello?", CancellationToken.None));

        var updated = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        var baRun = updated.Features[0].StageRuns.Single(sr => sr.StageName == "business-analyst");
        Assert.Equal(StageErrorKind.ProviderRejected, baRun.LastErrorKind);
        Assert.Equal(ReleaseStageStatus.Escalated, baRun.Status);
    }

    [Fact]
    public async Task SendMessage_AfterEscalation_ARetryMessageStillReachesTheSameStageRun()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        fake.ExceptionsToThrow.Enqueue(new AcpDisconnectedException("ACP process exited"));
        await Assert.ThrowsAsync<AcpDisconnectedException>(
            () => engine.SendMessageAsync(featureId, "hello?", CancellationToken.None));

        // The retry (no queued exception this time) must find the same, now-Escalated stage
        // run rather than throwing "no active stage run" — this is the actual retry path.
        var result = await engine.SendMessageAsync(featureId, "retry", CancellationToken.None);
        Assert.NotNull(result);

        var updated = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        var baRun = updated.Features[0].StageRuns.Single(sr => sr.StageName == "business-analyst");
        Assert.Equal(StageErrorKind.None, baRun.LastErrorKind);
        Assert.Equal(ReleaseStageStatus.Active, baRun.Status);
    }

    [Fact]
    public async Task SendMessage_PromptIsDetachedFromCallerCancellationToken()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        using var callerCts = new CancellationTokenSource();
        await engine.SendMessageAsync(featureId, "We need a login form", callerCts.Token);

        Assert.NotNull(_coordinator.LastPromptToken);
        callerCts.Cancel();
        Assert.False(_coordinator.LastPromptToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task Advance_RunsBuiltinGates()
    {
        _gateRunner.Results.Add(new GateResult(true, "Tests passed", "12 tests, all green"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

        Assert.True(_gateRunner.Requests.Count > 0);
        Assert.Contains(result.StageRuns, sr => sr.GateChecks.All(gc => gc.Passed));
    }

    [Fact]
    public async Task RunGates_CommitsWorkspaceChangesWhenGatesPass()
    {
        // The agent's file writes (BRS.md, manifest.yaml, etc.) are only working-tree
        // changes until something commits them — without this, GitFlow's later merge/push on
        // signoff has no actual history to carry forward.
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        Assert.Contains(_gitService.Commands, c => c.StartsWith("commit:"));
    }

    [Fact]
    public async Task RunGates_DoesNotCommitWhenGatesFail()
    {
        _gateRunner.Results.Add(new GateResult(false, "Tests failed", "3 tests failed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        Assert.DoesNotContain(_gitService.Commands, c => c.StartsWith("commit:"));
    }

    [Fact]
    public async Task Advance_MarksBlockedWhenGateFails()
    {
        _gateRunner.Results.Add(new GateResult(false, "Tests failed", "3 tests failed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

        Assert.Contains(result.StageRuns, sr => sr.Status == ReleaseStageStatus.BlockedGate);
    }

    [Fact]
    public async Task Advance_MarksBlockedWhenSignoffPending()
    {
        _gateRunner.Results.Add(new GateResult(true, "OK", "passed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

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
        var featureId = release.CurrentFeatureId!.Value;

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        var requiredSignoffs = release.Signoffs.Where(s => s.Required).ToList();
        foreach (var s in requiredSignoffs)
        {
            var updated = await engine.SignoffAsync(featureId, s.StageName, "qa-lead", "Looks good", CancellationToken.None);
            Assert.True(GetAllSignoffsAcrossFeatures(updated).First(x => x.StageName == s.StageName).Approved);
        }

        var final = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        Assert.NotEqual(ReleaseStatus.Blocked, final.Status);
    }

    // After the last stage's signoff, CurrentFeatureId is cleared and the feature moves to
    // Complete — so release.Signoffs (which proxies through CurrentFeature) goes empty. Fall
    // back to the completed feature's own Signoffs list in that case.
    private static IEnumerable<ReleaseSignoff> GetAllSignoffsAcrossFeatures(DevTeamRelease release) =>
        release.Features.SelectMany(f => f.Signoffs);

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
    public async Task ListReleases_NoWorkspaceFilter_ReturnsAllReleases()
    {
        var engine = CreateEngine();
        var r1 = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var r2 = await engine.StartReleaseAsync("feat-002", @"C:\work\proj", CancellationToken.None);

        var list = await engine.ListReleasesAsync(null, CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, r => r.Id == r1.Id);
        Assert.Contains(list, r => r.Id == r2.Id);
    }

    [Fact]
    public async Task ListReleases_WithWorkspaceFilter_ReturnsOnlyThatWorkspacesReleases()
    {
        var engine = CreateEngine();
        var projA = await engine.StartReleaseAsync("feat-a", @"C:\work\project-a", CancellationToken.None);
        var projB = await engine.StartReleaseAsync("feat-b", @"C:\work\project-b", CancellationToken.None);

        var list = await engine.ListReleasesAsync(@"C:\work\project-a", CancellationToken.None);

        Assert.Single(list);
        Assert.Equal(projA.Id, list[0].Id);
        Assert.DoesNotContain(list, r => r.Id == projB.Id);
    }

    [Fact]
    public async Task ListReleases_WorkspaceFilter_NormalizesSlashStyle()
    {
        // A release stored with backslashes must still match a query using forward
        // slashes (or vice versa) — the same path can be represented either way.
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-a", @"C:\work\project-a", CancellationToken.None);

        var list = await engine.ListReleasesAsync("C:/work/project-a", CancellationToken.None);

        Assert.Single(list);
        Assert.Equal(release.Id, list[0].Id);
    }

    [Fact]
    public async Task Advance_AdvancesStageIndex()
    {
        _gateRunner.Results.Add(new GateResult(true, "OK", "passed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        // BA stage requires user input — use interactive flow
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        var fetched = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        Assert.True(fetched.FlowPosition!.CurrentStageIndex >= 0);
    }

    [Fact]
    public async Task Signoff_AdvancesFlowPositionWhenCurrentStageSignsOff()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        // BA interactive flow → gates pass → await signoff
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var gated = await engine.RunGatesAsync(featureId, CancellationToken.None);
        Assert.Equal(ReleaseStatus.Blocked, gated.Status);

        var updated = await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        Assert.Equal(ReleaseStatus.InProgress, updated.Status);
        Assert.Equal(1, updated.FlowPosition!.CurrentStageIndex);
        Assert.Equal("developer", updated.FlowPosition.CurrentStageName);
    }

    [Fact]
    public async Task RunStage_RejectsUserInputStage()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunStageAsync(featureId, CancellationToken.None));
    }

    [Fact]
    public async Task RunStage_PersistsEachStepsGateCheckBeforeTheNextStepRuns()
    {
        // Regression: WorkflowEngine used to only flush gate-check rows to the DB at phase
        // boundaries (start/end of the whole step loop), so a concurrent poll (the frontend's
        // live step checklist) couldn't observe progress until the entire attempt finished.
        _gateRunner.Results.AddRange([
            new GateResult(true, "OK", "context bundle ok"),
            new GateResult(true, "OK", "verify ok"),
            new GateResult(true, "OK", "hygiene ok"),
            new GateResult(true, "OK", "slice ok"),
            new GateResult(true, "OK", "pr ok"),
        ]);

        var engine = CreateEngine();
        var (_, featureId) = await DriveToDeveloperAsync(engine);

        var savedCountsBeforeEachCall = new List<int>();
        _gateRunner.OnBeforeReturn = async (_) =>
        {
            await using var freshDb = CreateFactory().CreateDbContext();
            var count = await freshDb.ReleaseGateChecks.CountAsync(gc => gc.StageRun.StageName == "developer");
            savedCountsBeforeEachCall.Add(count);
        };

        await engine.RunStageAsync(featureId, CancellationToken.None);

        // 5 builtin steps run (context_bundle, verify_code, code_hygiene, slice_guard, render_pr);
        // each one's result must already be visible via a fresh context by the time the next
        // one's RunAsync fires — i.e. counts strictly increase, not [0,0,0,0,0].
        Assert.Equal([0, 1, 2, 3, 4], savedCountsBeforeEachCall);
    }

    [Fact]
    public async Task RunStage_ExecutesAutonomousStageAndBlocksOnSignoff()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        var updated = await engine.RunStageAsync(featureId, CancellationToken.None);
        var devRun = updated.StageRuns.Single(sr => sr.StageName == "developer");

        Assert.Equal(ReleaseStageStatus.BlockedSignoff, devRun.Status);
        Assert.Equal(StagePhase.Signoff, devRun.Phase);
        Assert.Equal(ReleaseStatus.Blocked, updated.Status);
        Assert.Equal(1, updated.FlowPosition!.CurrentStageIndex);
        Assert.True(devRun.ReadyToProceed);
        Assert.Contains(_gitService.Commands, c => c.StartsWith("commit:"));

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
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "tests failed"), 8));
        var updated = await engine.RunStageAsync(featureId, CancellationToken.None);
        var devRun = updated.StageRuns.Single(sr => sr.StageName == "developer");

        Assert.Equal(ReleaseStageStatus.BlockedGate, devRun.Status);
        Assert.False(devRun.ReadyToProceed);
    }

    [Fact]
    public async Task RunStage_SkipsTheAntagonistReviewWhenGatesAlreadyFailed()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "tests failed"), 8));
        await engine.RunStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        Assert.DoesNotContain(fake.Prompts, p => p.StartsWith("You are reviewing the developer's work"));
    }

    [Fact]
    public async Task RunStage_StillRunsTheAntagonistReviewWhenGatesPass()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        await engine.RunStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        Assert.Contains(fake.Prompts, p => p.StartsWith("You are reviewing the developer's work"));
    }

    [Fact]
    public async Task RunStage_TriesAgentLoopUpToAttempts()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "verify failed"), 8));
        await engine.RunStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        // developer loop attempts up to 3 → agent re-prompted
        var developerPrompts = fake.Prompts.Count(p => p.StartsWith("You are the developer"));
        Assert.Equal(3, developerPrompts);
    }

    [Fact]
    public async Task RunStage_FeedsAFailedLoopAttemptsEvidenceIntoTheNextAttemptsPrompt()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        // context_bundle (a leading builtin, runs before the loop) draws from the same fake
        // queue — give it a pass first, then fail verify_code on attempt 1 with distinctive
        // evidence; everything after falls through to the fake's default pass.
        _gateRunner.Results.Clear();
        _gateRunner.Results.Add(new GateResult(true, "OK", ""));
        _gateRunner.Results.Add(new GateResult(false, "Tests failed", "MARKER-attempt1-verify-code-failure"));

        await engine.RunStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        var developerPrompts = fake.Prompts.Where(p => p.StartsWith("You are the developer")).ToArray();
        Assert.True(developerPrompts.Length >= 2, "expected at least 2 developer prompts (attempt 1 + retry)");
        Assert.Contains("MARKER-attempt1-verify-code-failure", developerPrompts[1]);
    }

    [Fact]
    public async Task RunStage_ExhaustedLoop_DoesNotCrashAndStillBlocksOnGate()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "verify failed"), 8));

        var exception = await Record.ExceptionAsync(() => engine.RunStageAsync(featureId, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunStage_GateFailureNotes_AreAttributedToSystemNotUser()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "verify failed"), 8));
        var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

        var devRun = updated.StageRuns.Single(sr => sr.StageName == "developer");
        Assert.NotEmpty(devRun.GuidanceNotes);
        Assert.All(devRun.GuidanceNotes, n => Assert.Equal("system:gate-failure", n.AddedBy));
    }

    [Fact]
    public async Task RunStage_FailingEntryGate_NeverOpensASessionOrSendsAPrompt()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                entryGates:
                  - builtin: code_hygiene
                agent: { mode: researcher }
            """);

        try
        {
            _gateRunner.Results.Add(new GateResult(false, "bad", "leftover TODO"));

            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            Assert.Empty(_coordinator.Prompts);
            Assert.Equal(0, _coordinator.NewSessionCallCount);
            var stageRun = updated.StageRuns.Single(sr => sr.StageName == "researcher");
            Assert.Equal(ReleaseStageStatus.BlockedEntry, stageRun.Status);
            Assert.True(stageRun.GateChecks.Single().IsEntryGate);
            Assert.False(stageRun.GateChecks.Single().Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_PassingEntryGate_ProceedsNormallyAndOpensASession()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                signoff: reviewed
                entryGates:
                  - builtin: code_hygiene
                agent: { mode: researcher }
            """);

        try
        {
            // No queued failing result — FakeGateRunner defaults to Pass. A signoff is
            // required so the stage lands on BlockedSignoff instead of immediately completing
            // and finalizing the feature (DevTeamRelease.StageRuns only proxies the *current*
            // feature — once finalized, CurrentFeatureId clears and this would find nothing).
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            Assert.Single(_coordinator.Prompts);
            Assert.Equal(1, _coordinator.NewSessionCallCount);
            var stageRun = updated.StageRuns.Single(sr => sr.StageName == "researcher");
            Assert.NotEqual(ReleaseStageStatus.BlockedEntry, stageRun.Status);
            var entryCheck = stageRun.GateChecks.Single(gc => gc.IsEntryGate);
            Assert.True(entryCheck.Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_GatePromptExitStep_BehavesLikeChallenge_RecordingAFindingWhenNotEndTurn()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                steps:
                  - agent: { mode: researcher }
                  - gatePrompt: "Review the diff for security issues."
            """);

        try
        {
            // First prompt is the agent's own turn (stop reason irrelevant to its result);
            // second is the gate-prompt's own evaluation — this is the one that must fail.
            _coordinator.StopReasonsToReturn.Enqueue("end_turn");
            _coordinator.StopReasonsToReturn.Enqueue("max_tokens");

            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            var stageRun = updated.StageRuns.Single(sr => sr.StageName == "researcher");
            Assert.Equal(ReleaseStageStatus.BlockedGate, stageRun.Status);
            var gateCheck = stageRun.GateChecks.Single(gc => gc.Name.StartsWith("gate_prompt:"));
            Assert.False(gateCheck.Passed);
            Assert.NotEmpty(stageRun.Findings);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_RequiresSpecialistExitGate_BlocksWhenNoConsultationIsOnRecord()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                steps:
                  - agent: { mode: researcher }
                  - requiresSpecialist: database-admin
            """);

        try
        {
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            var stageRun = updated.StageRuns.Single(sr => sr.StageName == "researcher");
            Assert.Equal(ReleaseStageStatus.BlockedGate, stageRun.Status);
            var gateCheck = stageRun.GateChecks.Single(gc => gc.Name == "requires_specialist:database-admin");
            Assert.False(gateCheck.Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_RequiresSpecialistExitGate_PassesOnceTheAgentDelegatesWithinTheSameStageRun()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-requires-specialist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                signoff: reviewed
                steps:
                  - agent: { mode: researcher }
                  - requiresSpecialist: database-admin
            """);

        try
        {
            var featureDir = Path.Combine(workspace, "devteam", "features", "feat-001");
            Directory.CreateDirectory(featureDir);

            var specialistSessionId = Guid.NewGuid();
            await using (var db = CreateFactory().CreateDbContext())
            {
                db.SpecialistRoles.Add(new SpecialistRole { Name = "database-admin", Description = "DB expert", PrimingPrompt = "You are a DBA." });
                db.Sessions.Add(new DevTeamSession { Id = specialistSessionId, WorkspacePath = workspace, AcpSessionId = "acp-dba" });
                db.Messages.Add(new Message { SessionId = specialistSessionId, Role = "assistant", BodyText = "Add it nullable, backfill, then add the NOT NULL constraint." });
                await db.SaveChangesAsync();
            }

            // Pre-seeded so PromptWithDelegationAsync's post-prompt check sees it right after
            // the researcher's own first turn — simulating the agent writing it and ending its
            // turn, same convention the Part 3B delegation tests already rely on.
            File.WriteAllText(Path.Combine(featureDir, "delegate-request.json"),
                """{"role": "database-admin", "question": "How do I add a NOT NULL column safely?"}""");
            _coordinator.SessionIdsToReturn.Enqueue(Guid.NewGuid());
            _coordinator.SessionIdsToReturn.Enqueue(specialistSessionId);

            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            var stageRun = updated.StageRuns.Single(sr => sr.StageName == "researcher");
            var gateCheck = stageRun.GateChecks.Single(gc => gc.Name == "requires_specialist:database-admin");
            Assert.True(gateCheck.Passed);
            Assert.NotEqual(ReleaseStageStatus.BlockedGate, stageRun.Status);

            await using var verifyDb = CreateFactory().CreateDbContext();
            var consultation = await verifyDb.SpecialistConsultations.SingleAsync();
            Assert.Equal(stageRun.Id, consultation.StageRunId);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_RequiresArtifactExitGate_BlocksWhenTheDeclaredArtifactIsMissing()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: docs-root
                  fileName: codemap.json
                  kind: json
                steps:
                  - agent: { mode: code-map }
                  - requiresArtifact: code-map
            """);

        try
        {
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            var stageRun = updated.StageRuns.Single(sr => sr.StageName == "code-map");
            Assert.Equal(ReleaseStageStatus.BlockedGate, stageRun.Status);
            var gateCheck = stageRun.GateChecks.Single(gc => gc.Name == "requires_artifact:code-map");
            Assert.False(gateCheck.Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_RequiresArtifactExitGate_PassesOnceATextArtifactExists()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              code-map:
                signoff: reviewed
                artifact:
                  root: docs-root
                  fileName: codemap.md
                  kind: text
                steps:
                  - agent: { mode: code-map }
                  - requiresArtifact: code-map
            """);

        try
        {
            var artifactDir = Path.Combine(workspace, "docs", "code-map");
            Directory.CreateDirectory(artifactDir);
            File.WriteAllText(Path.Combine(artifactDir, "codemap.md"), "# Code Map\n");

            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            var stageRun = updated.StageRuns.Single(sr => sr.StageName == "code-map");
            var gateCheck = stageRun.GateChecks.Single(gc => gc.Name == "requires_artifact:code-map");
            Assert.True(gateCheck.Passed);
            Assert.NotEqual(ReleaseStageStatus.BlockedGate, stageRun.Status);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_RequiresArtifactExitGate_FailsForMalformedJsonAndPassesForValidJson()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              code-map:
                signoff: reviewed
                artifact:
                  root: docs-root
                  fileName: codemap.json
                  kind: json
                steps:
                  - agent: { mode: code-map }
                  - requiresArtifact: code-map
            """);

        try
        {
            var artifactDir = Path.Combine(workspace, "docs", "code-map");
            Directory.CreateDirectory(artifactDir);
            var artifactPath = Path.Combine(artifactDir, "codemap.json");
            File.WriteAllText(artifactPath, "{ not valid json");

            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var malformed = await engine.RunStageAsync(featureId, CancellationToken.None);
            var malformedRun = malformed.StageRuns.Single(sr => sr.StageName == "code-map");
            Assert.Equal(ReleaseStageStatus.BlockedGate, malformedRun.Status);
            Assert.False(malformedRun.GateChecks.Single(gc => gc.Name == "requires_artifact:code-map").Passed);

            File.WriteAllText(artifactPath, """{"modules": []}""");
            var fixedResult = await engine.RunStageAsync(featureId, CancellationToken.None);
            var fixedRun = fixedResult.StageRuns
                .Where(sr => sr.StageName == "code-map")
                .OrderByDescending(sr => sr.Attempt)
                .First();
            Assert.True(fixedRun.GateChecks.Single(gc => gc.Name == "requires_artifact:code-map").Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_FailingRequiresArtifactEntryGate_NeverOpensASessionOrSendsAPrompt()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: docs-root
                  fileName: codemap.json
                  kind: json
                agent: { mode: code-map }
              business-analyst:
                entryGates:
                  - requiresArtifact: code-map
                agent: { mode: business-analyst }
            """);

        try
        {
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            // code-map has no entry gates and no signoff, so its own turn (an agent step with
            // no exit gate) completes and the flow auto-advances straight to business-analyst.
            await engine.RunStageAsync(featureId, CancellationToken.None);
            _coordinator.Prompts.Clear();

            var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

            Assert.Empty(_coordinator.Prompts);
            var baRun = updated.StageRuns.Single(sr => sr.StageName == "business-analyst");
            Assert.Equal(ReleaseStageStatus.BlockedEntry, baRun.Status);
            Assert.True(baRun.GateChecks.Single().IsEntryGate);
            Assert.False(baRun.GateChecks.Single().Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_GatePromptStep_ResolvesFRootAndStageArtifactPlaceholders()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: docs-root
                  fileName: codemap.json
                  kind: json
                agent: { mode: code-map }
              researcher:
                entryGates:
                  - gatePrompt: "Feature <F>: confirm <docs-root> and <feature-docs-root> and <code-map/artifact.file> are all real paths."
                agent: { mode: researcher }
            """);

        try
        {
            _coordinator.StopReasonsToReturn.Enqueue("end_turn"); // code-map's own turn
            _coordinator.StopReasonsToReturn.Enqueue("end_turn"); // the entry gate prompt itself

            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            // code-map (index 0) completes and advances the flow position; researcher's entry
            // gate only runs once RunStageAsync is called again for the now-current stage.
            await engine.RunStageAsync(featureId, CancellationToken.None);
            await engine.RunStageAsync(featureId, CancellationToken.None);

            var gatePrompt = _coordinator.Prompts.Single(p => p.Contains("confirm"));
            Assert.Contains("Feature feat-001:", gatePrompt);
            Assert.Contains(Path.Combine(workspace, "docs"), gatePrompt);
            Assert.Contains(ArtifactPaths.FeatureDir(workspace, "feat-001"), gatePrompt);
            Assert.Contains(Path.Combine(workspace, "docs", "code-map", "codemap.json"), gatePrompt);
            Assert.DoesNotContain("<F>", gatePrompt);
            Assert.DoesNotContain("<docs-root>", gatePrompt);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_QaGateFailureWithResponsibleRoleDeveloper_RoutesBackToDeveloperInsteadOfLoopingQa()
    {
        var engine = CreateEngine();
        var (_, featureId) = await DriveToDeveloperAsync(engine);

        // developer stage: all gates pass by default (no queued failures).
        await engine.RunStageAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "developer", "tech-lead", null, CancellationToken.None);

        // qa stage: context_bundle and verify_code pass, coverage_matrix fails. QA's mandate
        // is to verify, not author tests, so this must route back to developer (who owns
        // verify_code/coverage_matrix per the default pipeline) instead of leaving QA in a
        // retry loop it can never win.
        _gateRunner.Results.Clear();
        _gateRunner.Results.Add(new GateResult(true, "OK", ""));
        _gateRunner.Results.Add(new GateResult(true, "OK", ""));
        _gateRunner.Results.Add(new GateResult(false, "missing coverage", "REQ-002 not covered"));

        var updated = await engine.RunStageAsync(featureId, CancellationToken.None);

        Assert.Equal(1, updated.FlowPosition!.CurrentStageIndex);
        Assert.Equal("developer", updated.FlowPosition.CurrentStageName);

        var qaRun = updated.StageRuns.Where(sr => sr.StageName == "qa").OrderByDescending(sr => sr.StartedAt).First();
        Assert.Equal(ReleaseStageStatus.BlockedGate, qaRun.Status);

        var note = updated.StageRuns
            .SelectMany(sr => sr.GuidanceNotes)
            .Single(n => n.AddedBy == "system:gate-failure" && n.Text.Contains("coverage_matrix"));
        Assert.Contains("missing coverage", note.Text);
        Assert.Contains("REQ-002 not covered", note.Text);
    }

    [Fact]
    public async Task RunStage_DelegatesToASpecialist_FoldsTheAnswerIntoAFollowUpPromptAndRecordsTheConsultation()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-delegation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var featureDir = Path.Combine(workspace, "devteam", "features", "feat-001");
            Directory.CreateDirectory(featureDir);

            var specialistSessionId = Guid.NewGuid();
            await using (var db = CreateFactory().CreateDbContext())
            {
                db.SpecialistRoles.Add(new SpecialistRole { Name = "database-admin", Description = "DB expert", PrimingPrompt = "You are a DBA." });
                db.Sessions.Add(new DevTeamSession { Id = specialistSessionId, WorkspacePath = workspace, AcpSessionId = "acp-dba" });
                db.Messages.Add(new Message { SessionId = specialistSessionId, Role = "assistant", BodyText = "Add it nullable, backfill, then add the NOT NULL constraint." });
                await db.SaveChangesAsync();
            }

            var engine = CreateEngine();
            // Drive through business-analyst first (its own turn also checks for delegation,
            // so the request file must not exist yet, or it would be consumed there instead).
            var (_, featureId) = await DriveToDeveloperAsync(engine, workspace);
            File.WriteAllText(Path.Combine(featureDir, "delegate-request.json"),
                """{"role": "database-admin", "question": "How do I add a NOT NULL column safely?"}""");
            // Developer's own session opens first (consuming a throwaway id); only the
            // specialist consultation itself should consume the seeded one.
            _coordinator.SessionIdsToReturn.Enqueue(Guid.NewGuid());
            _coordinator.SessionIdsToReturn.Enqueue(specialistSessionId);

            await engine.RunStageAsync(featureId, CancellationToken.None);

            // The developer session's turn should have been followed up with the specialist's answer.
            Assert.Contains(_coordinator.Prompts, p => p.Contains("Add it nullable, backfill, then add the NOT NULL constraint."));
            Assert.False(File.Exists(Path.Combine(featureDir, "delegate-request.json")));

            await using var verifyDb = CreateFactory().CreateDbContext();
            var consultation = await verifyDb.SpecialistConsultations.SingleAsync();
            Assert.Equal("database-admin", consultation.SpecialistName);
            Assert.Contains("NOT NULL column", consultation.Question);
            Assert.Contains("backfill", consultation.ResponseText);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_DelegationRequestForAnUnknownSpecialist_FoldsAnErrorIntoTheFollowUpInsteadOfCrashing()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-delegation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var featureDir = Path.Combine(workspace, "devteam", "features", "feat-001");
            Directory.CreateDirectory(featureDir);
            // A decoy specialist that exists but isn't the one requested, so this exercises
            // "unknown name" specifically rather than "no specialists registered at all".
            await using (var db = CreateFactory().CreateDbContext())
            {
                db.SpecialistRoles.Add(new SpecialistRole { Name = "database-admin", Description = "DB expert", PrimingPrompt = "You are a DBA." });
                await db.SaveChangesAsync();
            }

            var engine = CreateEngine();
            var (_, featureId) = await DriveToDeveloperAsync(engine, workspace);
            File.WriteAllText(Path.Combine(featureDir, "delegate-request.json"),
                """{"role": "network-engineer", "question": "Help"}""");
            var sessionCallsBefore = _coordinator.NewSessionCallCount;

            var result = await engine.RunStageAsync(featureId, CancellationToken.None);

            Assert.Contains(_coordinator.Prompts, p => p.Contains("Unknown specialist 'network-engineer'"));
            // Exactly 2 new sessions open for this stage regardless of delegation: developer's
            // own producer session, and its antagonist Challenge review (gates pass by
            // default in this test) — no third session for the unregistered specialist name.
            Assert.Equal(sessionCallsBefore + 2, _coordinator.NewSessionCallCount);
            Assert.NotNull(result);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_SpecialistSession_NeverReceivesTheCallingStagesWritePrefixes()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-delegation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var featureDir = Path.Combine(workspace, "devteam", "features", "feat-001");
            Directory.CreateDirectory(featureDir);
            // The specialist is docs-only (WritesCode: false) even though the calling
            // developer stage itself writes code — its scope must not inherit the caller's.
            var specialistSessionId = Guid.NewGuid();
            await using (var db = CreateFactory().CreateDbContext())
            {
                db.SpecialistRoles.Add(new SpecialistRole { Name = "database-admin", Description = "DB expert", PrimingPrompt = "You are a DBA.", WritesCode = false });
                db.Sessions.Add(new DevTeamSession { Id = specialistSessionId, WorkspacePath = workspace, AcpSessionId = "acp-dba" });
                db.Messages.Add(new Message { SessionId = specialistSessionId, Role = "assistant", BodyText = "Migration prepared." });
                await db.SaveChangesAsync();
            }

            var engine = CreateEngine();
            var (_, featureId) = await DriveToDeveloperAsync(engine, workspace);
            File.WriteAllText(Path.Combine(featureDir, "delegate-request.json"),
                """{"role": "database-admin", "question": "Prepare a migration."}""");
            _coordinator.AllowedWritePrefixesCalls.Clear();
            // Developer's own session opens first (consuming a throwaway id); only the
            // specialist consultation itself should consume the seeded one.
            _coordinator.SessionIdsToReturn.Enqueue(Guid.NewGuid());
            _coordinator.SessionIdsToReturn.Enqueue(specialistSessionId);

            await engine.RunStageAsync(featureId, CancellationToken.None);

            // First call is developer's own (code + docs); the specialist's own call must be
            // docs-only, never the developer's broader scope.
            var specialistPrefixes = _coordinator.AllowedWritePrefixesCalls[1];
            Assert.Equal([ArtifactPaths.FeatureDirRelative("feat-001"), "docs"], specialistPrefixes);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_DelegationRequestsPastTheCap_StopWithoutHangingAndTheFileIsCleanedUp()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-delegation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var featureDir = Path.Combine(workspace, "devteam", "features", "feat-001");
            Directory.CreateDirectory(featureDir);
            var requestPath = Path.Combine(featureDir, "delegate-request.json");
            const string requestJson = """{"role": "database-admin", "question": "Again?"}""";

            await using (var db = CreateFactory().CreateDbContext())
            {
                db.SpecialistRoles.Add(new SpecialistRole { Name = "database-admin", Description = "DB expert", PrimingPrompt = "You are a DBA." });
                await db.SaveChangesAsync();
            }

            // Every session this flow opens (developer's own, any antagonist review, every
            // specialist consultation) gets a seeded assistant response — avoids needing to
            // predict exactly which session id ends up being which across however many
            // sessions actually open (developer's own producer session plus its Challenge
            // review both open one each).
            _coordinator.OnNewSession = sessionId =>
            {
                using var db = CreateFactory().CreateDbContext();
                db.Sessions.Add(new DevTeamSession { Id = sessionId, WorkspacePath = workspace, AcpSessionId = sessionId.ToString() });
                db.Messages.Add(new Message { SessionId = sessionId, Role = "assistant", BodyText = "Here you go." });
                db.SaveChanges();
            };

            // Simulate an agent that re-requests the same specialist on every turn it gets —
            // its own opening turn, and every delegation follow-up — matched by prompt text
            // (not session id, for the same reason as above) so the round trip must still
            // terminate on its own rather than looping forever.
            _coordinator.OnPrompt = (_, text) =>
            {
                if (text.Contains("You are the developer") || text.Contains("specialist responded"))
                    File.WriteAllText(requestPath, requestJson);
            };

            var engine = CreateEngine();
            var (_, featureId) = await DriveToDeveloperAsync(engine, workspace);

            await engine.RunStageAsync(featureId, CancellationToken.None);

            // Bounded at MaxDelegationRoundTrips (3), and the file is force-cleaned afterward
            // rather than left dangling for the next stage run to trip over.
            await using var verifyDb = CreateFactory().CreateDbContext();
            var consultationCount = await verifyDb.SpecialistConsultations.CountAsync();
            Assert.Equal(3, consultationCount);
            Assert.False(File.Exists(requestPath));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task PushBack_MovesToPreviousStageAndRecordsGuidance()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "tests failed"), 8));
        await engine.RunStageAsync(featureId, CancellationToken.None);

        var updated = await engine.PushBackAsync(
            featureId, "business-analyst", "Rework: add email validation", CancellationToken.None);

        Assert.Equal(0, updated.FlowPosition!.CurrentStageIndex);
        Assert.Equal("business-analyst", updated.FlowPosition.CurrentStageName);
        Assert.Equal(ReleaseStatus.InProgress, updated.Status);
        Assert.False(updated.Signoffs.Single(s => s.StageName == "business-analyst").Approved);

        var devRun = updated.StageRuns.Single(sr => sr.StageName == "developer");
        // Alongside the user's push-back note, the failed loop attempts above (Part 3's
        // gate-failure feedback) also leave their own system-authored notes — assert the
        // push-back one specifically rather than assuming it's the only note.
        Assert.Contains(devRun.GuidanceNotes, n => n.Text == "Rework: add email validation" && n.AddedBy == "user");
    }

    [Fact]
    public async Task StartStage_IncludesFeedbackFromPushedBackRun()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        _gateRunner.Results.Clear();
        _gateRunner.Results.AddRange(Enumerable.Repeat(new GateResult(false, "failed", "tests failed"), 8));
        await engine.RunStageAsync(featureId, CancellationToken.None);
        await engine.PushBackAsync(featureId, "business-analyst", "Rework: add email validation", CancellationToken.None);

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var fake = Assert.IsType<FakeBrokerCoordinator>(_coordinator);
        Assert.Contains(fake.Prompts, p => p.Contains("add email validation"));
    }

    [Fact]
    public async Task PushBack_RejectsUnknownOrForwardTarget()
    {
        var engine = CreateEngine();
        var (release, featureId) = await DriveToDeveloperAsync(engine);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.PushBackAsync(featureId, "qa", null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.PushBackAsync(featureId, "nobody", null, CancellationToken.None));
    }

    [Fact]
    public async Task GetStageMessages_ReturnsLinkedSessionMessages()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);

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

        var messages = await engine.GetStageMessagesAsync(featureId, stageRun.Id, CancellationToken.None);

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
        var featureId = release.CurrentFeatureId!.Value;

        var pipeline = await engine.GetPipelineAsync(featureId, CancellationToken.None);

        Assert.Equal(3, pipeline.Count);
        Assert.Equal("business-analyst", pipeline[0].Name);
        Assert.True(pipeline[0].UserInputRequired);
        Assert.Equal("requirements-approval", pipeline[0].Signoff);
        Assert.Equal("developer", pipeline[1].Name);
        Assert.False(pipeline[1].UserInputRequired);
        Assert.Equal("qa", pipeline[2].Name);
    }

    [Fact]
    public async Task GetPipeline_ExposesExpectedArtifactsPerRole()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        var pipeline = await engine.GetPipelineAsync(featureId, CancellationToken.None);

        Assert.Equal(["devteam/features/<F>/specs.feature", "devteam/features/<F>/handoff.md"], pipeline[0].ExpectedArtifacts);
        Assert.Equal(["devteam/features/<F>/code/"], pipeline[1].ExpectedArtifacts);
        Assert.Equal(["devteam/features/<F>/coverage.md"], pipeline[2].ExpectedArtifacts);
    }

    [Fact]
    public async Task GetPipeline_ExposesFlattenedStepsPerRole_ForALiveChecklist()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        var pipeline = await engine.GetPipelineAsync(featureId, CancellationToken.None);

        Assert.Equal(
            ["scaffold_specs", "context_bundle", "agent:business-analyst", "gherkin_validator", "render_handoff"],
            pipeline[0].Steps);
        // The loop's inner steps are flattened once, not repeated per retry attempt.
        Assert.Equal(
            ["context_bundle", "agent:developer", "verify_code", "code_hygiene", "slice_guard", "render_pr"],
            pipeline[1].Steps);
        Assert.Equal(
            ["context_bundle", "agent:qa", "verify_code", "coverage_matrix", "render_handoff"],
            pipeline[2].Steps);
    }

    [Fact]
    public async Task StartRelease_UsesACustomDevteamReleaseYamlForTheInitialFlowPositionAndSignoffs()
    {
        // Regression: StartReleaseAsync used to call _loader.LoadDefault() directly instead
        // of going through LoadWorkflow(workspacePath) — a custom pipeline's first stage name
        // and signoffs were silently ignored, always seeded from the default business-analyst/
        // developer/qa pipeline no matter what devteam/release.yaml actually said.
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                signoff: reviewed
                agent: { mode: researcher }
            """);

        try
        {
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);

            Assert.Equal("researcher", release.FlowPosition!.CurrentStageName);
            Assert.Equal(0, release.FlowPosition.CurrentStageIndex);
            Assert.Contains(release.Signoffs, s => s.StageName == "researcher" && s.Required);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task GetPipeline_UsesACustomDevteamReleaseYamlWhenTheWorkspaceHasOne()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        var devteamDir = Path.Combine(workspace, "devteam");
        Directory.CreateDirectory(devteamDir);
        File.WriteAllText(Path.Combine(devteamDir, "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                agent: { mode: researcher }
            """);

        try
        {
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var pipeline = await engine.GetPipelineAsync(featureId, CancellationToken.None);

            Assert.Equal(["researcher"], pipeline.Select(p => p.Name).ToArray());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task GetPipeline_FallsBackToTheDefaultPipelineWhenNoReleaseYamlExists()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            var pipeline = await engine.GetPipelineAsync(featureId, CancellationToken.None);

            Assert.Equal(["business-analyst", "developer", "qa"], pipeline.Select(p => p.Name).ToArray());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task GetWorkspaceChanges_ReturnsRelativePathsFromGitStatus()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        _gitService.ChangedFiles = ["back-end/DevTeam.Broker/Features/Calc/Calculator.cs", "back-end/DevTeam.Tests/Features/Calc/CalculatorTests.cs"];

        var changes = await engine.GetWorkspaceChangesAsync(featureId, CancellationToken.None);

        Assert.Equal(_gitService.ChangedFiles, changes);
    }

    // ─── pipeline discipline: leading builtins, write scoping, artifact handoff ─

    [Fact]
    public async Task StartStage_RunsScaffoldSpecsAndContextBundleBeforeFirstPrompt()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);

        Assert.Equal(
            [BuiltinRegistry.ScaffoldSpecs, BuiltinRegistry.ContextBundle],
            _gateRunner.Requests.Select(r => r.Builtin).ToList());
        Assert.Equal(2, stageRun.GateChecks.Count);
        Assert.All(stageRun.GateChecks, gc => Assert.True(gc.Passed));
        Assert.Single(_coordinator.Prompts);
    }

    [Fact]
    public async Task StartStage_BusinessAnalystSession_RequestsFeatureDocsAndDocsRootPrefixesOnly()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        var prefixes = Assert.Single(_coordinator.AllowedWritePrefixesCalls);
        Assert.Equal([ArtifactPaths.FeatureDirRelative("feat-001"), "docs"], prefixes);
    }

    [Fact]
    public async Task RunStage_DeveloperSession_RequestsManifestCodePathsPlusFeatureAndDocsRootPrefixes()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(workspace.Path, "feat-001"),
            new SliceManifest("feat-001", "Login", "back-end/Features/login", "front-end/app/login", ["Program.cs"], "dotnet test"));

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        _coordinator.AllowedWritePrefixesCalls.Clear();
        await engine.RunStageAsync(featureId, CancellationToken.None);

        // The developer's own producer session is created first; a second, unrestricted
        // session may follow for the antagonist challenge review — that one is out of scope here.
        var prefixes = _coordinator.AllowedWritePrefixesCalls[0];
        Assert.Equal(
            new[] { ArtifactPaths.FeatureDirRelative("feat-001"), "docs", "back-end/Features/login", "front-end/app/login", "Program.cs" },
            prefixes);
    }

    [Fact]
    public async Task RunStage_QaSession_RequestsSameCodePathsAsDeveloper()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(workspace.Path, "feat-001"),
            new SliceManifest("feat-001", "Login", "back-end/Features/login", "front-end/app/login", ["Program.cs"], "dotnet test"));

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        await engine.RunStageAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "developer", "tech-lead", null, CancellationToken.None);

        _coordinator.AllowedWritePrefixesCalls.Clear();
        await engine.RunStageAsync(featureId, CancellationToken.None);

        // The qa producer session is created first; a second, unrestricted session may follow
        // for the challenge review — that one is out of scope here.
        var prefixes = _coordinator.AllowedWritePrefixesCalls[0];
        Assert.Equal(
            new[] { ArtifactPaths.FeatureDirRelative("feat-001"), "docs", "back-end/Features/login", "front-end/app/login", "Program.cs" },
            prefixes);
    }

    [Fact]
    public async Task RunStage_CustomRoleDeclaredWritesCode_GetsCodePathsEvenThoughItIsNotNamedDeveloperOrQa()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-workflow-" + Guid.NewGuid().ToString("N"));
        var devteamDir = Path.Combine(workspace, "devteam");
        Directory.CreateDirectory(devteamDir);
        File.WriteAllText(Path.Combine(devteamDir, "release.yaml"), """
            opinionated: false
            pipeline:
              researcher:
                writesCode: true
                agent: { mode: researcher }
            """);
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(workspace, "feat-001"),
            new SliceManifest("feat-001", "Login", "back-end/Features/login", "front-end/app/login", ["Program.cs"], "dotnet test"));

        try
        {
            var engine = CreateEngine();
            var release = await engine.StartReleaseAsync("feat-001", workspace, CancellationToken.None);
            var featureId = release.CurrentFeatureId!.Value;

            await engine.RunStageAsync(featureId, CancellationToken.None);

            var prefixes = _coordinator.AllowedWritePrefixesCalls[0];
            Assert.Equal(
                new[] { ArtifactPaths.FeatureDirRelative("feat-001"), "docs", "back-end/Features/login", "front-end/app/login", "Program.cs" },
                prefixes);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunStage_DeveloperFirstPrompt_ContainsContextMdContentVerbatim()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));
        var contextPath = ArtifactPaths.ContextPath(workspace.Path, "feat-001");
        Directory.CreateDirectory(Path.GetDirectoryName(contextPath)!);
        File.WriteAllText(contextPath, "MARKER-e8f3-allowed-working-areas");

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        await engine.RunStageAsync(featureId, CancellationToken.None);

        Assert.Contains(_coordinator.Prompts, p => p.Contains("MARKER-e8f3-allowed-working-areas"));
    }

    [Fact]
    public async Task RunGates_SetsReadyToProceed_WhenGatesPass()
    {
        // Passing gates is the sole authority on readiness — no separate "agent said DONE"
        // requirement. Requiring both was too fragile: real agent replies rarely match a literal
        // standalone "DONE" line, which left the human with no way to advance the stage at all.
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);

        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

        Assert.True(result.StageRuns.Single(sr => sr.StageName == "business-analyst").ReadyToProceed);
    }

    [Fact]
    public async Task RunGates_DoesNotSetReadyToProceed_WhenGatesFail()
    {
        _gateRunner.Results.Add(new GateResult(false, "Tests failed", "3 tests failed"));
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);

        var result = await engine.RunGatesAsync(featureId, CancellationToken.None);

        Assert.False(result.StageRuns.Single(sr => sr.StageName == "business-analyst").ReadyToProceed);
    }

    [Fact]
    public async Task SendMessage_InvalidatesReadyToProceed()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);

        await using (var db = CreateFactory().CreateDbContext())
        {
            var entity = await db.ReleaseStageRuns.SingleAsync(sr => sr.Id == stageRun.Id);
            entity.ReadyToProceed = true;
            await db.SaveChangesAsync();
        }

        await engine.SendMessageAsync(featureId, "Actually, one more thing", CancellationToken.None);

        await using (var db = CreateFactory().CreateDbContext())
        {
            var entity = await db.ReleaseStageRuns.SingleAsync(sr => sr.Id == stageRun.Id);
            Assert.False(entity.ReadyToProceed);
        }
    }


    [Fact]
    public async Task GetAvailableModels_ReturnsModelsFromCatalogForReleaseWorkspace()
    {
        _coordinator.ModelsToReturn = [new ModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5", "Anthropic")];
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        var models = await engine.GetAvailableModelsAsync(release.Id, CancellationToken.None);

        Assert.Single(models);
        Assert.Equal("claude-sonnet-4-5", models[0].Value);
    }

    [Fact]
    public async Task StartStage_RequestsAnExplicitDefaultModel_NotAmbientDefault()
    {
        // Leaving modelId null lets opencode fall back to whatever its own ambient
        // "current" model is, which can silently drift to something very slow. Always
        // ask for a known-good default explicitly instead.
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);

        Assert.All(_coordinator.RequestedModelIds, id => Assert.False(string.IsNullOrEmpty(id)));
    }

    [Fact]
    public async Task RunStage_RequestsAnExplicitDefaultModel_NotAmbientDefault()
    {
        var engine = CreateEngine();
        await DriveToDeveloperAsync(engine);

        Assert.All(_coordinator.RequestedModelIds, id => Assert.False(string.IsNullOrEmpty(id)));
    }

    [Fact]
    public async Task StartStage_PromptsBAgentToAuthorRequirementsArtifact()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        var initialPrompt = Assert.Single(_coordinator.Prompts);
        Assert.Contains("business-analyst", initialPrompt);
        Assert.Contains(ArtifactPaths.BrsFileName, initialPrompt);
        Assert.Contains("REQ-", initialPrompt);
        Assert.Contains("Given", initialPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunGates_FeedsRequirementsFromDiskToRequirementGates()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));
        var brsPath = ArtifactPaths.BrsPath(workspace.Path, "feat-001");
        Directory.CreateDirectory(Path.GetDirectoryName(brsPath)!);
        File.WriteAllText(brsPath,
            "## REQ-001: User can log in" + Environment.NewLine +
            "Given a registered user" + Environment.NewLine +
            "When they enter valid credentials" + Environment.NewLine +
            "Then they are signed in");

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

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
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        var gherkinRequest = _gateRunner.Requests.Single(r => r.Builtin == BuiltinRegistry.GherkinValidator);
        Assert.Equal("[]", gherkinRequest.Request.Inputs!["requirementsJson"]);
    }

    [Fact]
    public async Task RunGates_SuppliesTestArtifactsToCoverageGate()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-engine-" + Guid.NewGuid().ToString("N")));
        var brsPath = ArtifactPaths.BrsPath(workspace.Path, "feat-001");
        Directory.CreateDirectory(Path.GetDirectoryName(brsPath)!);
        File.WriteAllText(brsPath,
            "## REQ-F01: Addition works" + Environment.NewLine +
            "Given valid numbers, When they are combined, Then the sum is returned");
        Directory.CreateDirectory(Path.Combine(workspace.Path, "CalculatorLib.Tests"));
        File.WriteAllText(Path.Combine(workspace.Path, "CalculatorLib.Tests", "CalculatorTests.cs"), "REQ_F01_Add_ReturnsSum");
        File.WriteAllText(ArtifactPaths.ManifestPath(workspace.Path, "feat-001"), "feature: feat-001" + Environment.NewLine + "testCommand: dotnet --version" + Environment.NewLine);

        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a calculator", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "developer", "tech-lead", null, CancellationToken.None);

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        var coverageRequest = _gateRunner.Requests.Single(r => r.Builtin == BuiltinRegistry.CoverageMatrix);
        Assert.Contains("REQ_F01_Add_ReturnsSum", coverageRequest.Request.Inputs!["testFilesJson"]);
        Assert.Contains("testOutput", coverageRequest.Request.Inputs!.Keys);
    }

    // ─── GitFlow: feature lifecycle ──────────────────────────────────────

    [Fact]
    public async Task CreateFeature_WhenFeatureAlreadyInFlight_Throws()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);

        // A feature is already in flight (created together with the release).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.CreateFeatureAsync(release.Id, "feat-002", CancellationToken.None));
    }

    [Fact]
    public async Task CreateFeature_AfterFirstCompletes_StartsSecondFeatureIndependently()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        await CompleteFeatureThroughQaAsync(engine, release.CurrentFeatureId!.Value);

        var afterFirst = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        Assert.Null(afterFirst.CurrentFeatureId);
        Assert.Equal(ReleaseFeatureStatus.Complete, afterFirst.Features.Single(f => f.Key == "feat-001").Status);

        var feature2 = await engine.CreateFeatureAsync(release.Id, "feat-002", CancellationToken.None);
        Assert.Equal("feature/feat-002", feature2.BranchName);
        Assert.NotNull(feature2.FlowPosition);
        Assert.Equal(0, feature2.FlowPosition!.CurrentStageIndex);

        var afterSecond = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        Assert.Equal(feature2.Id, afterSecond.CurrentFeatureId);
        Assert.Equal(2, afterSecond.Features.Count);

        // Second feature's pipeline runs independently of the first's completed one.
        var stageRun = await engine.StartStageAsync(feature2.Id, CancellationToken.None);
        Assert.Equal("business-analyst", stageRun.StageName);
    }

    [Fact]
    public async Task Signoff_OnLastStage_MergesFeatureIntoReleaseAndDeletesBranches()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        var final = await CompleteFeatureThroughQaAsync(engine, featureId);

        Assert.Null(final.CurrentFeatureId);
        Assert.Equal(ReleaseStatus.Ready, final.Status);
        Assert.Equal(ReleaseFeatureStatus.Complete, final.Features.Single().Status);

        Assert.Contains("checkout:release/feat-001", _gitService.Commands);
        Assert.Contains("merge:feature/feat-001->release/feat-001", _gitService.Commands);
        Assert.Contains("push:release/feat-001", _gitService.Commands);
        Assert.Contains("delete-branch:feature/feat-001", _gitService.Commands);
    }

    [Fact]
    public async Task Signoff_OnLastStage_MergeConflict_DoesNotMarkFeatureComplete()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        _gitService.FailMerge = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CompleteFeatureThroughQaAsync(engine, featureId));

        var afterFailure = await engine.GetReleaseAsync(release.Id, CancellationToken.None);
        Assert.Equal(featureId, afterFailure.CurrentFeatureId);
        Assert.Equal(ReleaseFeatureStatus.InProgress, afterFailure.Features.Single().Status);
        Assert.DoesNotContain("delete-branch:feature/feat-001", _gitService.Commands);
    }

    [Fact]
    public async Task Signoff_OnLastStage_NoRemoteConfigured_SkipsPushButStillCompletes()
    {
        _gitService.RemoteConfigured = false;
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        var final = await CompleteFeatureThroughQaAsync(engine, featureId);

        Assert.Equal(ReleaseFeatureStatus.Complete, final.Features.Single().Status);
        Assert.Null(final.CurrentFeatureId);
        Assert.DoesNotContain(_gitService.Commands, c => c.StartsWith("push:release/"));
        // Local branch deletion still happens regardless of a remote.
        Assert.Contains("delete-branch:feature/feat-001", _gitService.Commands);
    }

    private async Task<DevTeamRelease> CompleteFeatureThroughQaAsync(WorkflowEngine engine, Guid featureId)
    {
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        await engine.RunStageAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "developer", "tech-lead", null, CancellationToken.None);

        await engine.RunStageAsync(featureId, CancellationToken.None);
        return await engine.SignoffAsync(featureId, "qa", "qa-lead", null, CancellationToken.None);
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

    private async Task<(DevTeamRelease Release, Guid FeatureId)> DriveToDeveloperAsync(WorkflowEngine engine, string workspacePath = @"C:\work\proj")
    {
        var release = await engine.StartReleaseAsync("feat-001", workspacePath, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        // BA interactive flow: gates pass (default) → signoff required → approve → advance to developer
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var gated = await engine.RunGatesAsync(featureId, CancellationToken.None);
        Assert.Equal(ReleaseStageStatus.BlockedSignoff,
            gated.StageRuns.Single(sr => sr.StageName == "business-analyst").Status);

        var after = await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);
        Assert.Equal(1, after.FlowPosition!.CurrentStageIndex);
        return (after, featureId);
    }

    private readonly FakeBrokerCoordinator _coordinator = new();

    private WorkflowEngine CreateEngine() => new(
        CreateFactory(), _gateRunner, _coordinator, _broadcaster,
        _gitService, new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
        new ModelCatalogService(_coordinator), _credentialStore);

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
    // Lets a test observe state (e.g. via a fresh DbContext) right before this call returns,
    // without needing real concurrency — RunStageAsync awaits this call synchronously, so
    // whatever the previous step already persisted is visible here if it was actually saved.
    public Func<string, Task>? OnBeforeReturn { get; set; }
    private int _index;

    public async Task<GateResult> RunAsync(string builtin, GateRequest request, CancellationToken cancellationToken)
    {
        Requests.Add((builtin, request));
        if (OnBeforeReturn is not null) await OnBeforeReturn(builtin);
        var result = _index < Results.Count ? Results[_index++] : new GateResult(true, "OK", "");
        return result;
    }
}

internal sealed class FakeGitCredentialStore : IGitCredentialStore
{
    private readonly Dictionary<string, string> _tokens = [];

    public bool HasToken(string name) => _tokens.ContainsKey(name);
    public void SetToken(string name, string token) => _tokens[name] = token;
    public string? TryGetToken(string name) => _tokens.TryGetValue(name, out var token) ? token : null;
    public IReadOnlyList<string> ListNames() => _tokens.Keys.ToArray();
}

internal sealed class FakeGitService : IGitService
{
    public List<string> Commands { get; } = [];
    public string[] ChangedFiles { get; set; } = [];

    public Task<GitResponse> InitAsync(string workspacePath, CancellationToken ct = default)
    {
        Commands.Add($"init:{workspacePath}");
        return Task.FromResult(new GitResponse(true, "Initialized", IsRepo: true));
    }

    public Task<GitResponse> StatusAsync(string workspacePath, CancellationToken ct = default)
    {
        Commands.Add($"status:{workspacePath}");
        return Task.FromResult(new GitResponse(true, "OK", IsRepo: true, IsClean: true, ChangedFiles: ChangedFiles));
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
        if (FailMerge) return Task.FromResult(new GitResponse(false, "merge failed: conflict"));
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

    public bool RemoteConfigured { get; set; } = true;
    public bool FailMerge { get; set; }
    public bool FailPush { get; set; }

    public Task<GitResponse> PushAsync(string workspacePath, string branchName, string? authToken, CancellationToken ct = default)
    {
        Commands.Add($"push:{branchName}");
        if (FailPush) return Task.FromResult(new GitResponse(false, "push failed"));
        return Task.FromResult(new GitResponse(true, $"Pushed '{branchName}'"));
    }

    public Task<GitResponse> DeleteBranchAsync(string workspacePath, string branchName, string? authToken, CancellationToken ct = default)
    {
        Commands.Add($"delete-branch:{branchName}");
        return Task.FromResult(new GitResponse(true, $"Deleted '{branchName}'"));
    }

    public Task<GitResponse> HasRemoteAsync(string workspacePath, CancellationToken ct = default)
    {
        Commands.Add("has-remote");
        return Task.FromResult(new GitResponse(true, "OK", HasRemote: RemoteConfigured));
    }

    public Task<GitResponse> GetRemoteAsync(string workspacePath, CancellationToken ct = default)
    {
        Commands.Add("get-remote");
        return Task.FromResult(new GitResponse(true, "OK", HasRemote: RemoteConfigured, RemoteUrl: RemoteConfigured ? "https://example.test/repo.git" : null));
    }

    public Task<GitResponse> SetRemoteAsync(string workspacePath, string remoteUrl, CancellationToken ct = default)
    {
        Commands.Add($"set-remote:{remoteUrl}");
        RemoteConfigured = true;
        return Task.FromResult(new GitResponse(true, "Remote set", HasRemote: true, RemoteUrl: remoteUrl));
    }
}

internal sealed class FakeBrokerCoordinator : IWorkflowCoordinator
{
    public List<string> Commands { get; } = [];

    public List<string> Prompts { get; } = [];

    public List<bool> PromptIsPriming { get; } = [];

    public CancellationToken? LastPromptToken { get; private set; }

    public IReadOnlyList<ModelOption> ModelsToReturn { get; set; } = [];

    public List<string?> RequestedModelIds { get; } = [];

    public List<IReadOnlyList<string>?> AllowedWritePrefixesCalls { get; } = [];

    // Dequeued (one per call) instead of returning a normal result, for tests exercising
    // WorkflowEngine's failure-classification/recovery behavior (Part 1).
    public Queue<Exception> ExceptionsToThrow { get; } = new();

    // Dequeued (one per call) for tests exercising gate-prompt/Challenge pass-fail — defaults
    // to "end_turn" (pass) when empty so existing tests are unaffected.
    public Queue<string> StopReasonsToReturn { get; } = new();

    public int NewSessionCallCount { get; private set; }

    // Dequeued (one per call) instead of a random Guid, for tests that need to know a
    // delegated/challenge session's id ahead of time (e.g. to seed its assistant response).
    public Queue<Guid> SessionIdsToReturn { get; } = new();

    // Invoked with the id a new session was just given (whether dequeued or randomly
    // generated) — lets a test react to *any* session opening (e.g. Challenge/antagonist
    // reviews the test isn't specifically tracking) without needing to predict every one
    // FakeBrokerCoordinator hands out via SessionIdsToReturn.
    public Action<Guid>? OnNewSession { get; set; }

    public Task<SessionSummary> NewSessionAsync(string workspacePath, string? modelId, IReadOnlyList<string>? allowedWritePrefixes, CancellationToken ct)
    {
        Commands.Add($"new-session:{workspacePath}");
        RequestedModelIds.Add(modelId);
        AllowedWritePrefixesCalls.Add(allowedWritePrefixes);
        NewSessionCallCount++;
        var sessionId = SessionIdsToReturn.Count > 0 ? SessionIdsToReturn.Dequeue() : Guid.NewGuid();
        OnNewSession?.Invoke(sessionId);
        return Task.FromResult(new SessionSummary(
            sessionId, "fake-acp", workspacePath, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ModelsToReturn, []));
    }

    public Task<string> SetModeAsync(Guid sessionId, string modeId, CancellationToken ct)
    {
        Commands.Add($"set-mode:{modeId}");
        return Task.FromResult(modeId);
    }

    // Invoked with (sessionId, text) on every prompt call, before the exception/stop-reason
    // handling below — lets a test simulate a side effect of "the agent responded" (e.g.
    // writing delegate-request.json again, to test the delegation round-trip cap).
    public Action<Guid, string>? OnPrompt { get; set; }

    public Task<PromptResponse> PromptWithSessionRecoveryAsync(Guid sessionId, string text, CancellationToken ct, bool isPriming = false)
    {
        LastPromptToken = ct;
        Prompts.Add(text);
        PromptIsPriming.Add(isPriming);
        Commands.Add($"prompt:{text[..Math.Min(50, text.Length)]}...");
        OnPrompt?.Invoke(sessionId, text);
        if (ExceptionsToThrow.Count > 0)
            throw ExceptionsToThrow.Dequeue();
        var stopReason = StopReasonsToReturn.Count > 0 ? StopReasonsToReturn.Dequeue() : "end_turn";
        return Task.FromResult(new PromptResponse(sessionId, stopReason, 10, 5, 15));
    }
}
