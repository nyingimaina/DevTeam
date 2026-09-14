using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevTeam.Tests;

public class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.AppFactory>
{
    private readonly AppFactory _factory;

    public ApiIntegrationTests(AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_ReportsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("ok", health!.Status);
        Assert.False(string.IsNullOrEmpty(health.Version));
    }

    [Fact]
    public async Task Info_ReportsAgentAndProtocol()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/info");
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<AgentViewModel>();
        Assert.Equal("1", info!.ProtocolVersion);
        Assert.Equal("FakeAgent", info.AgentName);
    }

    [Fact]
    public async Task SessionLifecycle_PromptStreamsAndPersists()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/sessions",
            new { workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var session = await create.Content.ReadFromJsonAsync<SessionSummary>();
        Assert.NotEqual(Guid.Empty, session!.SessionId);
        Assert.Single(session.Models);
        Assert.Equal("build", session.ModeId);
        Assert.Equal(2, session.Modes.Count);

        var prompt = await client.PostAsJsonAsync($"/api/sessions/{session.SessionId}/prompt",
            new { text = "hello world" });
        if (!prompt.IsSuccessStatusCode)
        {
            var body = await prompt.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Prompt failed: {(int)prompt.StatusCode} {body}");
        }
        var result = await prompt.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.Equal("end_turn", result!.StopReason);
        Assert.True(result.TotalTokens > 0);

        var detail = await client.GetFromJsonAsync<SessionDetail>($"/api/sessions/{session.SessionId}");
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Messages.Count);
        Assert.Equal("hello world", detail.Messages[0].BodyText);
        Assert.Equal("Hello from the fake agent", detail.Messages[1].BodyText);

        var listResponse = await client.GetAsync("/api/sessions");
        if (!listResponse.IsSuccessStatusCode)
        {
            var body = await listResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"List failed: {(int)listResponse.StatusCode} {body}");
        }
        var list = await listResponse.Content.ReadFromJsonAsync<SessionSummary[]>();
        Assert.Contains(list!, s => s.SessionId == session.SessionId);

        var setModel = await client.PostAsJsonAsync($"/api/sessions/{session.SessionId}/model",
            new { modelId = "opencode/big-pickle-v2" });
        setModel.EnsureSuccessStatusCode();

        var setMode = await client.PostAsJsonAsync($"/api/sessions/{session.SessionId}/mode",
            new { modeId = "plan" });
        setMode.EnsureSuccessStatusCode();

        var afterMode = await client.GetFromJsonAsync<SessionDetail>($"/api/sessions/{session.SessionId}");
        Assert.NotNull(afterMode);
        Assert.Equal("plan", afterMode!.ModeId);

        var delete = await client.DeleteAsync($"/api/sessions/{session.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await client.GetAsync($"/api/sessions/{session.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task UnknownSession_PromptReturns404()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/sessions/{Guid.NewGuid()}/prompt",
            new { text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EmptyText_Rejected()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/sessions",
            new { workspacePath = " " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FsRoots_ListsHome()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/fs/roots");
        response.EnsureSuccessStatusCode();
        var roots = await response.Content.ReadFromJsonAsync<FileSystemRootDto[]>();
        Assert.NotNull(roots);
        Assert.Contains(roots!, r => r.DisplayName == "Home");
    }

    [Fact]
    public async Task FsList_ReturnsEntriesSortedDirsFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "devteam-fsapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "zdir"));
            await File.WriteAllTextAsync(Path.Combine(root, "afile.txt"), "x");

            var client = _factory.CreateClient();
            var response = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(root)}");
            response.EnsureSuccessStatusCode();
            var entries = await response.Content.ReadFromJsonAsync<FileSystemEntryDto[]>();
            Assert.Equal(2, entries!.Length);
            Assert.Equal("zdir", entries[0].Name);
            Assert.Equal("directory", entries[0].Kind);
            Assert.Equal("afile.txt", entries[1].Name);
            Assert.Equal("file", entries[1].Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FsStat_ReportsGitRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "devteam-fsapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            var client = _factory.CreateClient();
            var stat = await client.GetFromJsonAsync<FileSystemStatDto>(
                $"/api/fs/stat?path={Uri.EscapeDataString(root)}");
            Assert.NotNull(stat);
            Assert.True(stat!.Exists);
            Assert.True(stat.IsGitRepository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FsMkdir_CreatesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "devteam-fsapi-" + Guid.NewGuid().ToString("N"));
        var created = Path.Combine(root, "new-folder");
        try
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/fs/mkdir", new { path = created });
            response.EnsureSuccessStatusCode();
            Assert.True(Directory.Exists(created));
            var stat = await response.Content.ReadFromJsonAsync<FileSystemStatDto>();
            Assert.True(stat!.Exists);
            Assert.Equal("directory", stat.Kind);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FsList_MissingPath_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(@"C:\does-not-exist-devteam")}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseLifecycle_CreateListGetAdvanceSignoff()
    {
        var client = _factory.CreateClient();

        // Create release
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-api-001", workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
        Assert.NotNull(release);
        Assert.Equal("feat-api-001", release!.Features[0].Key);
        Assert.Equal(ReleaseStatus.InProgress, release.Status);

        // List releases
        var list = await client.GetFromJsonAsync<DevTeamRelease[]>("/api/releases");
        Assert.NotNull(list);
        Assert.Contains(list!, r => r.Id == release.Id);

        // Get release by ID
        var get = await client.GetFromJsonAsync<DevTeamRelease>($"/api/releases/{release.Id}");
        Assert.NotNull(get);
        Assert.Equal(release.Id, get!.Id);

        // Interactive flow: start stage → send message → run gates
        var startStage = await client.PostAsJsonAsync($"/api/releases/{release.Id}/start-stage", new { });
        startStage.EnsureSuccessStatusCode();

        var sendMessage = await client.PostAsJsonAsync($"/api/releases/{release.Id}/send-message",
            new { text = "We need a login form for users" });
        sendMessage.EnsureSuccessStatusCode();

        var runGates = await client.PostAsJsonAsync($"/api/releases/{release.Id}/run-gates", new { });
        runGates.EnsureSuccessStatusCode();
        var advanced = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>();
        Assert.NotNull(advanced);
        Assert.True(advanced!.StageRuns.Count > 0);

        // Signoff (if required)
        if (advanced.Status == ReleaseStatus.Blocked)
        {
            var signoff = advanced.Signoffs.First(s => s.Required && !s.Approved);
            var signoffResp = await client.PostAsJsonAsync($"/api/releases/{release.Id}/signoff",
                new { stageName = signoff.StageName, role = "qa-lead", comment = "Looks good" });
            signoffResp.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ReleaseCreate_MissingFeatureKey_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = " ", workspacePath = @"C:\work\test" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseCreate_MissingWorkspacePath_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-001", workspacePath = " " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseAdvance_NotFound_Returns404()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/releases/{Guid.NewGuid()}/advance", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseSignoff_MissingStageName_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/releases/{Guid.NewGuid()}/signoff",
            new { stageName = " ", role = "qa-lead", comment = "ok" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task E2E_FullReleaseLifecycle()
    {
        var client = _factory.CreateClient();

        // 1. Health
        var health = await client.GetAsync("/healthz");
        health.EnsureSuccessStatusCode();

        // 2. Git init
        var tmpDir = Path.Combine(Path.GetTempPath(), "e2e-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var gitInit = await client.PostAsJsonAsync("/api/git/init", new { workspacePath = tmpDir });
            var gitInitBody = await gitInit.Content.ReadAsStringAsync();
            Assert.True(gitInit.IsSuccessStatusCode, $"Git init failed: {gitInit.StatusCode} {gitInitBody}");
            var gitStatus = await client.GetFromJsonAsync<GitResponse>($"/api/git/status?workspacePath={Uri.EscapeDataString(tmpDir)}");
            Assert.True(gitStatus!.IsRepo);

            // 3. Git branches
            var gitBranch = await client.PostAsJsonAsync("/api/git/branch", new { workspacePath = tmpDir, branchName = "feature/test" });
            gitBranch.EnsureSuccessStatusCode();

            // 4. Create release
            var create = await client.PostAsJsonAsync("/api/releases",
                new { featureKey = "e2e-test", workspacePath = tmpDir });
            create.EnsureSuccessStatusCode();
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(release);
            Assert.Equal("e2e-test", release!.Features[0].Key);

            // 5. List releases
            var list = await client.GetFromJsonAsync<DevTeamRelease[]>("/api/releases");
            Assert.Contains(list!, r => r.Id == release.Id);

            // 6. Get release
            var get = await client.GetFromJsonAsync<DevTeamRelease>($"/api/releases/{release.Id}");
            Assert.Equal(release.Id, get!.Id);

            // 7. Start BA stage
            var startStage = await client.PostAsJsonAsync($"/api/releases/{release.Id}/start-stage", new { });
            startStage.EnsureSuccessStatusCode();

            // 8. Send message to BA agent
            var sendMessage = await client.PostAsJsonAsync($"/api/releases/{release.Id}/send-message",
                new { text = "We need a login form with email and password" });
            sendMessage.EnsureSuccessStatusCode();

            // 9. Run gates on BA stage
            var runGates = await client.PostAsJsonAsync($"/api/releases/{release.Id}/run-gates", new { });
            runGates.EnsureSuccessStatusCode();
            var afterGates = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(afterGates);

            // 10. Signoff BA stage
            if (afterGates.Status == ReleaseStatus.Blocked)
            {
                var signoff = afterGates.Signoffs.First(s => s.Required && !s.Approved);
                var signoffResp = await client.PostAsJsonAsync($"/api/releases/{release.Id}/signoff",
                    new { stageName = signoff.StageName, role = "pm", comment = "Approved" });
                signoffResp.EnsureSuccessStatusCode();
            }

            // 11. Git log
            var gitLog = await client.GetFromJsonAsync<GitResponse>($"/api/git/log?workspacePath={Uri.EscapeDataString(tmpDir)}");
            Assert.NotNull(gitLog);
            Assert.True(gitLog!.Success);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PipelineEndpoint_ReturnsOrderedStages()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-pipe-001", workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
        Assert.NotNull(release);

        var pipeline = await client.GetFromJsonAsync<PipelineStageDto[]>($"/api/releases/{release!.Id}/pipeline");
        Assert.NotNull(pipeline);
        Assert.Equal(3, pipeline!.Length);
        Assert.Equal("business-analyst", pipeline[0].Name);
        Assert.True(pipeline[0].UserInputRequired);
        Assert.Equal("requirements-approval", pipeline[0].Signoff);
        Assert.Equal("developer", pipeline[1].Name);
        Assert.False(pipeline[1].UserInputRequired);
        Assert.Equal("pr-created", pipeline[1].Signoff);
        Assert.Equal("qa", pipeline[2].Name);
        Assert.False(pipeline[2].UserInputRequired);
        Assert.Equal("release-approval", pipeline[2].Signoff);
    }

    [Fact]
    public async Task PipelineEndpoint_UnknownRelease_Returns404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/releases/{Guid.NewGuid()}/pipeline");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BAProgression_SeededRequirements_PassesGates_ThenBlocksOnSignoff()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-ba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var client = _factory.CreateClient();
            // Real gates read requirements from the workspace during the BA gate run.
            var requirementsPath = DevTeam.Broker.Gates.ArtifactPaths.RequirementsPath(workspace, "feat-ba-001");
            Directory.CreateDirectory(Path.GetDirectoryName(requirementsPath)!);
            await File.WriteAllTextAsync(requirementsPath,
                "## REQ-001: User can log in" + Environment.NewLine +
                "Given a registered user" + Environment.NewLine +
                "When they enter valid credentials" + Environment.NewLine +
                "Then they are signed in");

            var create = await client.PostAsJsonAsync("/api/releases",
                new { featureKey = "feat-ba-001", workspacePath = workspace });
            create.EnsureSuccessStatusCode();
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(release);

            (await client.PostAsJsonAsync($"/api/releases/{release!.Id}/start-stage", new { })).EnsureSuccessStatusCode();
            var send = await client.PostAsJsonAsync($"/api/releases/{release.Id}/send-message",
                new { text = "We need a login form with email and password" });
            send.EnsureSuccessStatusCode();

            var runGates = await client.PostAsJsonAsync($"/api/releases/{release.Id}/run-gates", new { });
            runGates.EnsureSuccessStatusCode();
            var after = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(after);

            var baRun = after!.StageRuns.Single(sr => sr.StageName == "business-analyst");
            Assert.Equal(ReleaseStageStatus.BlockedSignoff, baRun.Status);
            Assert.Equal(DevTeam.Broker.Domain.ReleaseStatus.Blocked, after.Status);
            Assert.True(baRun.GateChecks.All(gc => gc.Passed));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BAProgression_WithoutRequirements_GatesFail()

    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-ba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var client = _factory.CreateClient();
            var create = await client.PostAsJsonAsync("/api/releases",
                new { featureKey = "feat-ba-002", workspacePath = workspace });
            create.EnsureSuccessStatusCode();
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(release);

            (await client.PostAsJsonAsync($"/api/releases/{release!.Id}/start-stage", new { })).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync($"/api/releases/{release.Id}/send-message",
                new { text = "We need a login form" })).EnsureSuccessStatusCode();

            var runGates = await client.PostAsJsonAsync($"/api/releases/{release.Id}/run-gates", new { });
            runGates.EnsureSuccessStatusCode();
            var after = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(after);

            var baRun = after!.StageRuns.Single(sr => sr.StageName == "business-analyst");
            Assert.Equal(ReleaseStageStatus.BlockedGate, baRun.Status);
            Assert.Contains(baRun.GateChecks, gc => gc.Name == "gherkin_validator" && !gc.Passed);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunStage_InteractiveStage_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-run-001", workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
        Assert.NotNull(release);

        var response = await client.PostAsJsonAsync($"/api/releases/{release!.Id}/run-stage", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StageMessages_ReturnsLinkedSessionConversation()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-msg-001", workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
        Assert.NotNull(release);

        var start = await client.PostAsJsonAsync($"/api/releases/{release!.Id}/start-stage", new { });
        start.EnsureSuccessStatusCode();
        var stageRun = await start.Content.ReadFromJsonAsync<ReleaseStageRun>();
        Assert.NotNull(stageRun);
        Assert.NotNull(stageRun!.AcpSessionId);

        var send = await client.PostAsJsonAsync($"/api/releases/{release.Id}/send-message",
            new { text = "We need a login form" });
        send.EnsureSuccessStatusCode();

        var messages = await client.GetFromJsonAsync<MessageDto[]>(
            $"/api/releases/{release.Id}/stages/{stageRun.Id}/messages");
        Assert.NotNull(messages);
        Assert.Contains(messages!, m => m.BodyText?.Contains("We need a login form") == true);
        Assert.Contains(messages!, m => m.BodyText?.Contains("Hello from the fake agent") == true);
    }

    [Fact]
    public async Task RunStage_FailsGates_ThenPushBack_ReworksAndRestarts()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-runstage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var client = _factory.CreateClient();
            var create = await client.PostAsJsonAsync("/api/releases",
                new { featureKey = "feat-rw-001", workspacePath = workspace });
            create.EnsureSuccessStatusCode();
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(release);

            // BA progression (interactive + agent requirements extraction) is covered by the
            // engine tests with fake gates; here we seed the flow position to the developer
            // stage so the autonomous run/push-back HTTP surface can be exercised end to end.
            await SeedDeveloperPositionAsync(release!.Id);

            // 1. Autonomous developer run: gates fail in an empty workspace (no code) → BlockedGate
            var runDev = await client.PostAsJsonAsync($"/api/releases/{release.Id}/run-stage", new { });
            runDev.EnsureSuccessStatusCode();
            var afterDev = await runDev.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(afterDev);
            Assert.Equal(ReleaseStageStatus.BlockedGate,
                afterDev!.StageRuns.Single(sr => sr.StageName == "developer").Status);
            Assert.Equal(1, afterDev.FlowPosition!.CurrentStageIndex);

            // 2. Push back to BA with instructions
            var push = await client.PostAsJsonAsync($"/api/releases/{release.Id}/push-back",
                new { targetStageName = "business-analyst", instructions = "Rework: add email validation" });
            push.EnsureSuccessStatusCode();
            var rewritten = await push.Content.ReadFromJsonAsync<DevTeamRelease>();
            Assert.NotNull(rewritten);
            Assert.Equal(0, rewritten!.FlowPosition!.CurrentStageIndex);
            Assert.Equal("business-analyst", rewritten.FlowPosition.CurrentStageName);
            Assert.Equal(ReleaseStatus.InProgress, rewritten.Status);
            Assert.False(rewritten.Signoffs.Single(s => s.StageName == "business-analyst").Approved);
            var devRun = rewritten.StageRuns.Single(sr => sr.StageName == "developer");
            Assert.Equal("Rework: add email validation", devRun.GuidanceNotes.Single().Text);

            // 3. Re-starting BA after push-back creates a fresh active attempt
            var restart = await client.PostAsJsonAsync($"/api/releases/{release.Id}/start-stage", new { });
            restart.EnsureSuccessStatusCode();
            var baRun2 = await restart.Content.ReadFromJsonAsync<ReleaseStageRun>();
            Assert.Equal(1, baRun2!.Attempt);
            Assert.Equal(ReleaseStageStatus.Active, baRun2.Status);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PushBack_Validation_ReturnsExpectedStatusCodes()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-pb-001", workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>();
        Assert.NotNull(release);

        var forward = await client.PostAsJsonAsync($"/api/releases/{release!.Id}/push-back",
            new { targetStageName = "qa", instructions = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, forward.StatusCode);

        var unknown = await client.PostAsJsonAsync($"/api/releases/{release.Id}/push-back",
            new { targetStageName = "nobody", instructions = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var missing = await client.PostAsJsonAsync($"/api/releases/{release.Id}/push-back",
            new { targetStageName = " " });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var noRelease = await client.PostAsJsonAsync($"/api/releases/{Guid.NewGuid()}/push-back",
            new { targetStageName = "business-analyst", instructions = "x" });
        Assert.Equal(HttpStatusCode.NotFound, noRelease.StatusCode);
    }

    private async Task SeedDeveloperPositionAsync(Guid releaseId)
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var release = await db.Releases.Include(r => r.FlowPosition).SingleAsync(r => r.Id == releaseId);
        release.FlowPosition!.CurrentStageIndex = 1;
        release.FlowPosition.CurrentStageName = "developer";
        await db.SaveChangesAsync();
    }

    public class AppFactory : WebApplicationFactory<Program>
    {
        public string DatabasePath { get; } =
            Path.Combine(Path.GetTempPath(), "devteam-api-" + Guid.NewGuid().ToString("N") + ".db");

        public AppFactory()
        {
            using var db = new DevTeamDbContext(new DbContextOptionsBuilder<DevTeamDbContext>()
                .UseSqlite($"Data Source={DatabasePath}")
                .Options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DataDirectory", Path.GetDirectoryName(DatabasePath) ?? ".");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(d =>
                    d.ServiceType == typeof(IDbContextFactory<DevTeamDbContext>));
                services.Remove(descriptor);

                var connection = $"Data Source={DatabasePath}";
                services.AddDbContextFactory<DevTeamDbContext>(options => options.UseSqlite(connection));

                services.RemoveAll<IAgentSpoke>();
                services.AddSingleton<IAgentSpoke, FakeAgentSpoke>();
            });
        }
    }

    private sealed class FakeAgentSpoke : IAgentSpoke
    {
        private int _sessionCounter;

        public event EventHandler<AgentEvent>? EventReceived;

        public Task<AgentInfo> InitializeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentInfo("FakeAgent", "9.9.9"));

        public async Task<AgentSession> NewSessionAsync(string cwd, CancellationToken cancellationToken)
        {
            var sessionId = $"ses_{Interlocked.Increment(ref _sessionCounter)}";
            return new AgentSession(sessionId,
            [
                new AgentConfigOption(
                    "model", "Model", "model", "select",
                    "opencode/big-pickle",
                    [new AgentConfigOptionValue("opencode/big-pickle", "OpenCode Big Pickle", null)]),
                new AgentConfigOption(
                    "mode", "Mode", "provider", "select",
                    "build",
                    [
                        new AgentConfigOptionValue("build", "Build", null),
                        new AgentConfigOptionValue("plan", "Plan", null),
                    ]),
            ]);
        }

        public async Task<AgentPromptResult> PromptAsync(
            string sessionId, IReadOnlyList<AgentPromptPart> prompt, CancellationToken cancellationToken)
        {
            foreach (var part in prompt)
            {
                var chunk = new AgentTextDelta(sessionId, $"msg_{part.Text[..1]}", "Hello from the fake agent");
                EventReceived?.Invoke(this, chunk);
            }

            return new AgentPromptResult(
                "end_turn",
                new AgentUsageInfo(InputTokens: 30, OutputTokens: 5, TotalTokens: 35, CachedReadTokens: null),
                UserMessageId: null);
        }

        public Task SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FakeEventBroadcaster : IEventBroadcaster
    {
        public List<StreamEvent> Events { get; } = [];

        public Task BroadcastAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
        {
            Events.Add(streamEvent);
            return Task.CompletedTask;
        }

        public Task BroadcastToReleaseAsync(Guid releaseId, StreamEvent streamEvent, CancellationToken cancellationToken)
        {
            Events.Add(streamEvent);
            return Task.CompletedTask;
        }
    }
}
