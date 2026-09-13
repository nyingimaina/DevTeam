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