using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevTeam.Tests;

public class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.AppFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly AppFactory _factory;

    public ApiIntegrationTests(AppFactory factory) => _factory = factory;

    private static void TryDeleteDirectory(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var attrs = File.GetAttributes(file);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (i < 4)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(200);
            }
        }
    }

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
    public async Task Root_ServesIndexHtml()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<title>DevTeam</title>", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Root_WithoutWwwRoot_DoesNotCrash()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "devteam-noroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.UseSpaFallback();
            await app.StartAsync();

            using var client = new HttpClient();
            try
            {
                var response = await client.GetAsync(app.Urls.First() + "/");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ApiRequests_CarryDiagnosticHeaders()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/info");

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Request-Elapsed-Ms", out var elapsed));
        Assert.True(long.TryParse(elapsed!.Single(), out var elapsedMs) && elapsedMs >= 0);
        Assert.True(response.Headers.TryGetValues("X-Request-Declared-Length", out var declared));
        Assert.Equal("none", declared!.Single());
    }

    [Fact]
    public async Task ApiRequests_ReportDeclaredContentLengthForPosts()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-content-length", workspacePath = @"C:\work\api-test" });

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Request-Declared-Length", out var declared));
        Assert.NotEqual("none", declared!.Single());
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

    // Characterization test (written before the Chat tab is removed): /model and /mode are
    // shared with ReleaseWizard's stage-header ModelPicker, so they must survive the Chat
    // tab's deletion. This obtains a session id via the release pipeline (start-stage), not
    // via the Chat-exclusive POST /api/sessions route, so it stays valid after that route is gone.
    [Fact]
    public async Task SessionModelAndMode_ReachableViaSessionCreatedByReleasePipeline()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-model-mode", workspacePath = @"C:\work\model-mode-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);

        var startStage = await client.PostAsJsonAsync($"/api/features/{release!.CurrentFeatureId}/start-stage", new { });
        startStage.EnsureSuccessStatusCode();
        var stageRun = await startStage.Content.ReadFromJsonAsync<ReleaseStageRun>(JsonOptions);
        Assert.NotNull(stageRun);
        var sessionId = stageRun!.AcpSessionId;
        Assert.NotNull(sessionId);

        var setModel = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/model",
            new { modelId = "opencode/big-pickle-v2" });
        setModel.EnsureSuccessStatusCode();
        var modelBody = await setModel.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("opencode/big-pickle-v2", modelBody.GetProperty("modelId").GetString());

        var setMode = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/mode",
            new { modeId = "plan" });
        setMode.EnsureSuccessStatusCode();
        var modeBody = await setMode.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan", modeBody.GetProperty("modeId").GetString());

        // GET is what ModelPicker actually calls (via getSessionAsync) to read the current
        // model/mode when it first mounts — not just the two POST routes above.
        var getSession = await client.GetAsync($"/api/sessions/{sessionId}");
        getSession.EnsureSuccessStatusCode();
        var sessionDetail = await getSession.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("opencode/big-pickle-v2", sessionDetail.GetProperty("modelId").GetString());
        Assert.Equal("plan", sessionDetail.GetProperty("modeId").GetString());
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
    public async Task FsReveal_ExistingDirectory_LaunchesExplorer()
    {
        var root = Path.Combine(Path.GetTempPath(), "devteam-fsapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/fs/reveal", new { path = root });
            response.EnsureSuccessStatusCode();
            Assert.Equal(1, _factory.ProcessLauncher.CallCount);
            Assert.Equal("explorer.exe", _factory.ProcessLauncher.LastFileName);
            Assert.Equal(Path.GetFullPath(root), _factory.ProcessLauncher.LastArgument);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FsReveal_MissingPath_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/fs/reveal",
            new { path = Path.Combine(Path.GetTempPath(), "devteam-fsapi-missing-" + Guid.NewGuid().ToString("N")) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GitRemote_NoneConfigured_ReturnsNullUrl()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "devteam-gitremote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var client = _factory.CreateClient();
            (await client.PostAsJsonAsync("/api/git/init", new { workspacePath = tmpDir })).EnsureSuccessStatusCode();

            var remote = await client.GetFromJsonAsync<GitRemoteResponse>(
                $"/api/git/remote?workspacePath={Uri.EscapeDataString(tmpDir)}");
            Assert.NotNull(remote);
            Assert.Null(remote!.Url);
        }
        finally
        {
            TryDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitRemote_SetWithCredentialName_RoundTripsUrlAndCredentialName()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "devteam-gitremote-" + Guid.NewGuid().ToString("N"));
        var bareDir = Path.Combine(Path.GetTempPath(), "devteam-gitremote-bare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(bareDir);
        try
        {
            var client = _factory.CreateClient();
            (await client.PostAsJsonAsync("/api/git/init", new { workspacePath = tmpDir })).EnsureSuccessStatusCode();

            var setResponse = await client.PostAsJsonAsync("/api/git/remote",
                new { workspacePath = tmpDir, url = bareDir, credentialName = "github-personal" });
            setResponse.EnsureSuccessStatusCode();
            var setBody = await setResponse.Content.ReadFromJsonAsync<GitRemoteResponse>();
            Assert.Equal(bareDir, setBody!.Url);
            Assert.Equal("github-personal", setBody.CredentialName);

            var getResponse = await client.GetFromJsonAsync<GitRemoteResponse>(
                $"/api/git/remote?workspacePath={Uri.EscapeDataString(tmpDir)}");
            Assert.Equal(bareDir, getResponse!.Url);
            Assert.Equal("github-personal", getResponse.CredentialName);
        }
        finally
        {
            TryDeleteDirectory(tmpDir);
            TryDeleteDirectory(bareDir);
        }
    }

    [Fact]
    public async Task GitCredential_SetThenList_ReturnsNameNeverToken()
    {
        var client = _factory.CreateClient();
        var setResponse = await client.PostAsJsonAsync("/api/git/credential",
            new { name = "github-personal-" + Guid.NewGuid().ToString("N"), token = "ghp_supersecrettoken" });
        setResponse.EnsureSuccessStatusCode();
        var setBody = await setResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("supersecrettoken", setBody);

        var list = await client.GetFromJsonAsync<GitCredentialsResponse>("/api/git/credentials");
        Assert.NotNull(list);
        Assert.Contains(list!.Names, n => n.StartsWith("github-personal-"));
    }

    [Fact]
    public async Task GitCredential_MissingToken_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/git/credential", new { name = "x", token = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FsList_MissingPath_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(@"C:\does-not-exist-devteam")}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FsCleanup_NoMatches_ReturnsEmptyList()
    {
        _factory.CleanupService.Result = [];
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/fs/cleanup", new { workspacePath = @"D:\apps\tictactoe" });

        response.EnsureSuccessStatusCode();
        var stopped = await response.Content.ReadFromJsonAsync<StoppedProcessDto[]>();
        Assert.Empty(stopped!);
    }

    [Fact]
    public async Task FsCleanup_WithMatches_ReturnsStoppedProcesses()
    {
        _factory.CleanupService.Result = [new StoppedProcessDto(4242, "node.exe")];
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/fs/cleanup", new { workspacePath = @"D:\apps\tictactoe" });

        response.EnsureSuccessStatusCode();
        var stopped = await response.Content.ReadFromJsonAsync<StoppedProcessDto[]>();
        Assert.Single(stopped!);
        Assert.Equal(4242, stopped![0].ProcessId);
        Assert.Equal("node.exe", stopped[0].Name);
        Assert.Equal(@"D:\apps\tictactoe", _factory.CleanupService.LastWorkspacePath);
    }

    [Fact]
    public async Task FsCleanup_MissingWorkspacePath_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/fs/cleanup", new { workspacePath = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FsCleanup_RelativeWorkspacePath_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/fs/cleanup", new { workspacePath = "relative/path" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StartStage_WhenTurnIsCancelledMidFlight_DoesNotSurfaceARaw500()
    {
        // AgentSpoke is shared across every test in this class (IClassFixture) — always
        // reset ShouldHang so a failure here can't leave every later test hanging forever.
        _factory.AgentSpoke.ShouldHang = true;
        try
        {
            var client = _factory.CreateClient();

            var create = await client.PostAsJsonAsync("/api/releases",
                new { featureKey = "cancel-test", workspacePath = @"C:\work\cancel-test-" + Guid.NewGuid().ToString("N") });
            create.EnsureSuccessStatusCode();
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);

            var startTask = client.PostAsync($"/api/features/{release!.CurrentFeatureId}/start-stage", null);

            ActiveTurnInfo? turn = null;
            for (var i = 0; i < 100 && turn is null; i++)
            {
                await Task.Delay(20);
                var turnResponse = await client.GetAsync("/api/turns/current");
                if (turnResponse.StatusCode == HttpStatusCode.OK)
                    turn = await turnResponse.Content.ReadFromJsonAsync<ActiveTurnInfo>();
            }
            Assert.NotNull(turn);

            var cancelResponse = await client.PostAsync("/api/turns/current/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

            var startResponse = await startTask;
            Assert.NotEqual(HttpStatusCode.InternalServerError, startResponse.StatusCode);

            var afterCancel = await client.GetAsync("/api/turns/current");
            Assert.Equal(HttpStatusCode.NoContent, afterCancel.StatusCode);
        }
        finally
        {
            _factory.AgentSpoke.ShouldHang = false;
        }
    }

    [Fact]
    public async Task TurnsCurrent_WithNothingRunning_ReturnsNoContent()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/turns/current");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TurnsCancel_WithNothingRunning_ReturnsNotFound()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/turns/current/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListReleases_FilteredByWorkspacePath_ReturnsOnlyThatWorkspacesReleases()
    {
        var client = _factory.CreateClient();
        var workspaceA = @"C:\work\project-a-" + Guid.NewGuid().ToString("N");
        var workspaceB = @"C:\work\project-b-" + Guid.NewGuid().ToString("N");

        var createA = await client.PostAsJsonAsync("/api/releases", new { featureKey = "feat-a", workspacePath = workspaceA });
        createA.EnsureSuccessStatusCode();
        var releaseA = await createA.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);

        var createB = await client.PostAsJsonAsync("/api/releases", new { featureKey = "feat-b", workspacePath = workspaceB });
        createB.EnsureSuccessStatusCode();
        var releaseB = await createB.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);

        var filtered = await client.GetFromJsonAsync<DevTeamRelease[]>(
            $"/api/releases?workspacePath={Uri.EscapeDataString(workspaceA)}", JsonOptions);

        Assert.NotNull(filtered);
        Assert.Contains(filtered!, r => r.Id == releaseA!.Id);
        Assert.DoesNotContain(filtered!, r => r.Id == releaseB!.Id);
    }

    [Fact]
    public async Task ReleaseLifecycle_CreateListGetAdvanceSignoff()
    {
        var client = _factory.CreateClient();

        // Create release
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-api-001", workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);
        Assert.Equal("feat-api-001", release!.Features[0].Key);
        Assert.Equal(ReleaseStatus.InProgress, release.Status);

        // List releases
        var list = await client.GetFromJsonAsync<DevTeamRelease[]>("/api/releases", JsonOptions);
        Assert.NotNull(list);
        Assert.Contains(list!, r => r.Id == release.Id);

        // Get release by ID
        var get = await client.GetFromJsonAsync<DevTeamRelease>($"/api/releases/{release.Id}", JsonOptions);
        Assert.NotNull(get);
        Assert.Equal(release.Id, get!.Id);

        // Interactive flow: start stage → send message → run gates
        var featureId = release.CurrentFeatureId;
        var startStage = await client.PostAsJsonAsync($"/api/features/{featureId}/start-stage", new { });
        startStage.EnsureSuccessStatusCode();

        var sendMessage = await client.PostAsJsonAsync($"/api/features/{featureId}/send-message",
            new { text = "We need a login form for users" });
        sendMessage.EnsureSuccessStatusCode();

        var runGates = await client.PostAsJsonAsync($"/api/features/{featureId}/run-gates", new { });
        runGates.EnsureSuccessStatusCode();
        var advanced = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(advanced);
        Assert.True(advanced!.StageRuns.Count > 0);

        // Signoff (if required)
        if (advanced.Status == ReleaseStatus.Blocked)
        {
            var signoff = advanced.Signoffs.First(s => s.Required && !s.Approved);
            var signoffResp = await client.PostAsJsonAsync($"/api/features/{featureId}/signoff",
                new { stageName = signoff.StageName, role = "qa-lead", comment = "Looks good" });
            signoffResp.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task GetRelease_SerializesEnumsAsStringsNotIntegers()
    {
        var client = _factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-enum-json", workspacePath = @"C:\work\enum-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);

        var get = await client.GetAsync($"/api/releases/{release!.Id}");
        get.EnsureSuccessStatusCode();
        var rawJson = await get.Content.ReadAsStringAsync();

        Assert.Contains("\"status\":\"InProgress\"", rawJson);
        Assert.DoesNotContain("\"status\":0", rawJson);
        Assert.DoesNotContain("\"status\":1", rawJson);
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
        var response = await client.PostAsJsonAsync($"/api/features/{Guid.NewGuid()}/advance", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseSignoff_MissingStageName_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/features/{Guid.NewGuid()}/signoff",
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
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
            Assert.NotNull(release);
            Assert.Equal("e2e-test", release!.Features[0].Key);

            // 5. List releases
            var list = await client.GetFromJsonAsync<DevTeamRelease[]>("/api/releases", JsonOptions);
            Assert.Contains(list!, r => r.Id == release.Id);

            // 6. Get release
            var get = await client.GetFromJsonAsync<DevTeamRelease>($"/api/releases/{release.Id}", JsonOptions);
            Assert.Equal(release.Id, get!.Id);

            // 7. Start BA stage
            var featureId = release.CurrentFeatureId;
            var startStage = await client.PostAsJsonAsync($"/api/features/{featureId}/start-stage", new { });
            startStage.EnsureSuccessStatusCode();

            // 8. Send message to BA agent
            var sendMessage = await client.PostAsJsonAsync($"/api/features/{featureId}/send-message",
                new { text = "We need a login form with email and password" });
            sendMessage.EnsureSuccessStatusCode();

            // 9. Run gates on BA stage
            var runGates = await client.PostAsJsonAsync($"/api/features/{featureId}/run-gates", new { });
            runGates.EnsureSuccessStatusCode();
            var afterGates = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
            Assert.NotNull(afterGates);

            // 10. Signoff BA stage
            if (afterGates.Status == ReleaseStatus.Blocked)
            {
                var signoff = afterGates.Signoffs.First(s => s.Required && !s.Approved);
                var signoffResp = await client.PostAsJsonAsync($"/api/features/{featureId}/signoff",
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
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);

        var pipeline = await client.GetFromJsonAsync<PipelineStageDto[]>($"/api/features/{release!.CurrentFeatureId}/pipeline", JsonOptions);
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
        var response = await client.GetAsync($"/api/features/{Guid.NewGuid()}/pipeline");
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
            // Real gates read the BRS from the workspace during the BA gate run.
            var brsPath = DevTeam.Broker.Gates.ArtifactPaths.BrsPath(workspace, "feat-ba-001");
            Directory.CreateDirectory(Path.GetDirectoryName(brsPath)!);
            await File.WriteAllTextAsync(brsPath,
                "## REQ-001: User can log in" + Environment.NewLine +
                "Given a registered user" + Environment.NewLine +
                "When they enter valid credentials" + Environment.NewLine +
                "Then they are signed in");

            var create = await client.PostAsJsonAsync("/api/releases",
                new { featureKey = "feat-ba-001", workspacePath = workspace });
            create.EnsureSuccessStatusCode();
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
            Assert.NotNull(release);

            var featureId = release!.CurrentFeatureId;
            (await client.PostAsJsonAsync($"/api/features/{featureId}/start-stage", new { })).EnsureSuccessStatusCode();
            var send = await client.PostAsJsonAsync($"/api/features/{featureId}/send-message",
                new { text = "We need a login form with email and password" });
            send.EnsureSuccessStatusCode();

            var runGates = await client.PostAsJsonAsync($"/api/features/{featureId}/run-gates", new { });
            runGates.EnsureSuccessStatusCode();
            var after = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
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
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
            Assert.NotNull(release);

            var featureId = release!.CurrentFeatureId;
            (await client.PostAsJsonAsync($"/api/features/{featureId}/start-stage", new { })).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync($"/api/features/{featureId}/send-message",
                new { text = "We need a login form" })).EnsureSuccessStatusCode();

            var runGates = await client.PostAsJsonAsync($"/api/features/{featureId}/run-gates", new { });
            runGates.EnsureSuccessStatusCode();
            var after = await runGates.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
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
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);

        var response = await client.PostAsJsonAsync($"/api/features/{release!.CurrentFeatureId}/run-stage", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StageMessages_ReturnsLinkedSessionConversation()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/releases",
            new { featureKey = "feat-msg-001", workspacePath = @"C:\work\api-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);

        var featureId = release!.CurrentFeatureId;
        var start = await client.PostAsJsonAsync($"/api/features/{featureId}/start-stage", new { });
        start.EnsureSuccessStatusCode();
        var stageRun = await start.Content.ReadFromJsonAsync<ReleaseStageRun>(JsonOptions);
        Assert.NotNull(stageRun);
        Assert.NotNull(stageRun!.AcpSessionId);

        var send = await client.PostAsJsonAsync($"/api/features/{featureId}/send-message",
            new { text = "We need a login form" });
        send.EnsureSuccessStatusCode();

        var messages = await client.GetFromJsonAsync<MessageDto[]>(
            $"/api/features/{featureId}/stages/{stageRun.Id}/messages");
        Assert.NotNull(messages);
        var userReply = messages!.Single(m => m.BodyText?.Contains("We need a login form") == true);
        Assert.False(userReply.IsPriming);
        Assert.Contains(messages!, m => m.BodyText?.Contains("Hello from the fake agent") == true);

        var openingPrompt = messages!.Single(m => m.BodyText?.StartsWith("You are the business-analyst") == true);
        Assert.True(openingPrompt.IsPriming);
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
            var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
            Assert.NotNull(release);

            // BA progression (interactive + agent requirements extraction) is covered by the
            // engine tests with fake gates; here we seed the flow position to the developer
            // stage so the autonomous run/push-back HTTP surface can be exercised end to end.
            var featureId = release!.CurrentFeatureId!.Value;
            await SeedDeveloperPositionAsync(featureId);

            // 1. Autonomous developer run: gates fail in an empty workspace (no code) → BlockedGate
            var runDev = await client.PostAsJsonAsync($"/api/features/{featureId}/run-stage", new { });
            runDev.EnsureSuccessStatusCode();
            var afterDev = await runDev.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
            Assert.NotNull(afterDev);
            Assert.Equal(ReleaseStageStatus.BlockedGate,
                afterDev!.StageRuns.Single(sr => sr.StageName == "developer").Status);
            Assert.Equal(1, afterDev.FlowPosition!.CurrentStageIndex);

            // 2. Push back to BA with instructions
            var push = await client.PostAsJsonAsync($"/api/features/{featureId}/push-back",
                new { targetStageName = "business-analyst", instructions = "Rework: add email validation" });
            push.EnsureSuccessStatusCode();
            var rewritten = await push.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
            Assert.NotNull(rewritten);
            Assert.Equal(0, rewritten!.FlowPosition!.CurrentStageIndex);
            Assert.Equal("business-analyst", rewritten.FlowPosition.CurrentStageName);
            Assert.Equal(ReleaseStatus.InProgress, rewritten.Status);
            Assert.False(rewritten.Signoffs.Single(s => s.StageName == "business-analyst").Approved);
            var devRun = rewritten.StageRuns.Single(sr => sr.StageName == "developer");
            // Alongside the user's push-back note, failed loop attempts also leave their own
            // system-authored gate-failure notes — assert the push-back one specifically.
            Assert.Contains(devRun.GuidanceNotes, n => n.Text == "Rework: add email validation" && n.AddedBy == "user");

            // 3. Re-starting BA after push-back creates a fresh active attempt
            var restart = await client.PostAsJsonAsync($"/api/features/{featureId}/start-stage", new { });
            restart.EnsureSuccessStatusCode();
            var baRun2 = await restart.Content.ReadFromJsonAsync<ReleaseStageRun>(JsonOptions);
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
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);

        var featureId = release!.CurrentFeatureId;
        var forward = await client.PostAsJsonAsync($"/api/features/{featureId}/push-back",
            new { targetStageName = "qa", instructions = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, forward.StatusCode);

        var unknown = await client.PostAsJsonAsync($"/api/features/{featureId}/push-back",
            new { targetStageName = "nobody", instructions = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var missing = await client.PostAsJsonAsync($"/api/features/{featureId}/push-back",
            new { targetStageName = " " });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var noRelease = await client.PostAsJsonAsync($"/api/features/{Guid.NewGuid()}/push-back",
            new { targetStageName = "business-analyst", instructions = "x" });
        Assert.Equal(HttpStatusCode.NotFound, noRelease.StatusCode);
    }

    /// <summary>Profile tests share one DB across the whole test class (see AppFactory) — clear
    /// existing rows first so "first profile becomes default"-style assertions are deterministic
    /// regardless of what other tests in this class created earlier.</summary>
    private async Task ClearProfilesAsync()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.WorkspaceProfileSettings.RemoveRange(db.WorkspaceProfileSettings);
        db.ProfilePrompts.RemoveRange(db.ProfilePrompts);
        db.Profiles.RemoveRange(db.Profiles);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Profiles_FirstOneCreated_BecomesDefaultAutomatically()
    {
        await ClearProfilesAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/profiles", new { name = "First", description = "d" });
        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.NotNull(profile);
        Assert.True(profile!.IsDefault);
        // No stage names are pre-seeded — a profile is reusable across workspaces with
        // different (possibly custom) pipelines, so there's no fixed list to seed from.
        // Saving a prompt for any stage name (see Profiles_UpdatePrompts_ThenSetDefault_RoundTrips)
        // adds its row on demand.
        Assert.Empty(profile.Prompts);

        var second = await client.PostAsJsonAsync("/api/profiles", new { name = "Second", description = "d2" });
        var secondProfile = await second.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.False(secondProfile!.IsDefault);
    }

    [Fact]
    public async Task Profiles_UpdatePrompts_ThenSetDefault_RoundTrips()
    {
        await ClearProfilesAsync();
        var client = _factory.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/profiles", new { name = "Terse", description = "" }))
            .Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);

        var updateResponse = await client.PutAsJsonAsync($"/api/profiles/{created!.Id}", new
        {
            name = "Terse",
            description = "Short answers",
            prompts = new[] { new { stageName = "developer", promptText = "Be concise." } },
        });
        updateResponse.EnsureSuccessStatusCode();
        var updated = await updateResponse.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.Equal("Be concise.", updated!.Prompts.Single(p => p.StageName == "developer").PromptText);

        var other = await (await client.PostAsJsonAsync("/api/profiles", new { name = "Other", description = "" }))
            .Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.False(other!.IsDefault);

        var overrideResponse = await client.PutAsJsonAsync($"/api/profiles/{created.Id}", new
        {
            name = "Terse",
            description = "Short answers",
            prompts = new[] { new { stageName = "developer", promptText = "Be concise.", overridesBuiltInPrompt = true } },
        });
        overrideResponse.EnsureSuccessStatusCode();
        var overridden = await overrideResponse.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.True(overridden!.Prompts.Single(p => p.StageName == "developer").OverridesBuiltInPrompt);

        var setDefault = await client.PostAsync($"/api/profiles/{other.Id}/default", null);
        setDefault.EnsureSuccessStatusCode();
        var afterSetDefault = await setDefault.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.True(afterSetDefault!.IsDefault);

        var list = await client.GetFromJsonAsync<ProfileDto[]>("/api/profiles", JsonOptions);
        Assert.Single(list!, p => p.IsDefault);
        Assert.Equal(other.Id, list!.Single(p => p.IsDefault).Id);
    }

    [Fact]
    public async Task Profiles_SavingAPromptForAStageNameNotInTheDefaultPipeline_StillPersists()
    {
        // Profiles aren't pinned to the default business-analyst/developer/qa names — a
        // workspace with a custom devteam/release.yaml can have entirely different stage
        // names, and saving a prompt for one must not be silently dropped.
        await ClearProfilesAsync();
        var client = _factory.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/profiles", new { name = "Custom", description = "" }))
            .Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);

        var updateResponse = await client.PutAsJsonAsync($"/api/profiles/{created!.Id}", new
        {
            name = "Custom",
            description = "",
            prompts = new[] { new { stageName = "researcher", promptText = "Dig deep." } },
        });
        updateResponse.EnsureSuccessStatusCode();
        var updated = await updateResponse.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.Equal("Dig deep.", updated!.Prompts.Single(p => p.StageName == "researcher").PromptText);
    }

    [Fact]
    public async Task Profiles_DeletingTheDefault_PromotesAnotherRemainingProfile()
    {
        await ClearProfilesAsync();
        var client = _factory.CreateClient();

        var first = await (await client.PostAsJsonAsync("/api/profiles", new { name = "First", description = "" }))
            .Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        var second = await (await client.PostAsJsonAsync("/api/profiles", new { name = "Second", description = "" }))
            .Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        Assert.True(first!.IsDefault);
        Assert.False(second!.IsDefault);

        var delete = await client.DeleteAsync($"/api/profiles/{first.Id}");
        delete.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<ProfileDto[]>("/api/profiles", JsonOptions);
        var remaining = Assert.Single(list!);
        Assert.Equal(second.Id, remaining.Id);
        Assert.True(remaining.IsDefault);
    }

    [Fact]
    public async Task WorkspacePipeline_GetThenPut_RoundTripsAReorderedPipelineThroughTheRealHttpApi()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-pipeline-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var client = _factory.CreateClient();

        try
        {
            var loaded = await client.GetFromJsonAsync<PipelineEditorDto>(
                $"/api/workspace/pipeline?workspacePath={Uri.EscapeDataString(workspace)}", JsonOptions);
            Assert.Equal(["business-analyst", "developer", "qa"], loaded!.Roles.Select(r => r.Name).ToArray());

            var reordered = new[] { loaded.Roles[2], loaded.Roles[1], loaded.Roles[0] };
            var putResponse = await client.PutAsJsonAsync("/api/workspace/pipeline", new
            {
                workspacePath = workspace,
                roles = reordered,
            }, JsonOptions);
            putResponse.EnsureSuccessStatusCode();

            var afterPut = await putResponse.Content.ReadFromJsonAsync<PipelineEditorDto>(JsonOptions);
            Assert.Equal(["qa", "developer", "business-analyst"], afterPut!.Roles.Select(r => r.Name).ToArray());

            var reGet = await client.GetFromJsonAsync<PipelineEditorDto>(
                $"/api/workspace/pipeline?workspacePath={Uri.EscapeDataString(workspace)}", JsonOptions);
            Assert.Equal(["qa", "developer", "business-analyst"], reGet!.Roles.Select(r => r.Name).ToArray());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceProfile_WithNoExplicitSetting_ResolvesToAndPersistsTheGlobalDefault()
    {
        await ClearProfilesAsync();
        var client = _factory.CreateClient();
        var defaultProfile = await (await client.PostAsJsonAsync("/api/profiles", new { name = "Default", description = "" }))
            .Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        var workspacePath = @"C:\work\profile-test-" + Guid.NewGuid().ToString("N");

        var response = await client.GetFromJsonAsync<WorkspaceProfileDto>(
            $"/api/workspace/profile?workspacePath={Uri.EscapeDataString(workspacePath)}", JsonOptions);
        Assert.Equal(defaultProfile!.Id, response!.ProfileId);

        // Explicitly overriding it to a different profile persists and is returned on re-read.
        var other = await (await client.PostAsJsonAsync("/api/profiles", new { name = "Other", description = "" }))
            .Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        var setResponse = await client.PostAsJsonAsync("/api/workspace/profile", new { workspacePath, profileId = other!.Id });
        setResponse.EnsureSuccessStatusCode();

        var reRead = await client.GetFromJsonAsync<WorkspaceProfileDto>(
            $"/api/workspace/profile?workspacePath={Uri.EscapeDataString(workspacePath)}", JsonOptions);
        Assert.Equal(other.Id, reRead!.ProfileId);
    }

    private async Task SeedDeveloperPositionAsync(Guid featureId)
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var feature = await db.ReleaseFeatures.Include(f => f.FlowPosition).SingleAsync(f => f.Id == featureId);
        feature.FlowPosition!.CurrentStageIndex = 1;
        feature.FlowPosition.CurrentStageName = "developer";
        await db.SaveChangesAsync();
    }

    public class AppFactory : WebApplicationFactory<Program>
    {
        public string DatabasePath { get; } =
            Path.Combine(Path.GetTempPath(), "devteam-api-" + Guid.NewGuid().ToString("N") + ".db");

        public FakeWorkspaceProcessCleanupService CleanupService { get; } = new();
        public FakeAgentSpoke AgentSpoke { get; } = new();
        public FakeProcessLauncher ProcessLauncher { get; } = new();

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
                services.AddSingleton<IAgentSpoke>(AgentSpoke);

                services.RemoveAll<IWorkspaceProcessCleanupService>();
                services.AddSingleton<IWorkspaceProcessCleanupService>(CleanupService);

                services.RemoveAll<IProcessLauncher>();
                services.AddSingleton<IProcessLauncher>(ProcessLauncher);
            });
        }
    }

    public sealed class FakeProcessLauncher : IProcessLauncher
    {
        public int CallCount { get; private set; }
        public string? LastFileName { get; private set; }
        public string? LastArgument { get; private set; }

        public void Launch(string fileName, string argument)
        {
            CallCount++;
            LastFileName = fileName;
            LastArgument = argument;
        }
    }

    public sealed class FakeWorkspaceProcessCleanupService : IWorkspaceProcessCleanupService
    {
        public IReadOnlyList<StoppedProcessDto> Result { get; set; } = [];
        public string? LastWorkspacePath { get; private set; }

        public IReadOnlyList<StoppedProcessDto> CleanupWorkspace(string workspacePath)
        {
            LastWorkspacePath = workspacePath;
            return Result;
        }
    }

    public sealed class FakeAgentSpoke : IAgentSpoke
    {
        private int _sessionCounter;

        /// <summary>When true, PromptAsync hangs until its token is cancelled, to test the cancel-turn flow.</summary>
        public bool ShouldHang { get; set; }

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
            if (ShouldHang)
                await Task.Delay(Timeout.Infinite, cancellationToken);

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
