using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Workflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevTeam.Tests;

/// <summary>
/// The second half of the in-the-wild agent-launch bug, and the one the lazy wiring did not cover.
///
/// <see cref="ApiUnlaunchableAgentTests"/> proves a read-only endpoint no longer touches the agent.
/// This file covers the endpoints that legitimately do: when the CLI is installed but Windows
/// refuses to start it, <c>POST /api/features/{id}/start-stage</c> answered a bare
/// <c>500</c> with a stack trace and no way for the UI to say anything useful.
///
/// The expectation is a <c>503</c> naming opencode and how to reinstall it, and — the part that
/// matters for the next bug — the same answer from <em>any</em> endpoint that needs the agent, with
/// no per-endpoint code involved.
/// </summary>
public sealed class AgentLaunchFailureApiTests : IClassFixture<AgentLaunchFailureApiTests.Factory>
{
    private readonly Factory _factory;

    public AgentLaunchFailureApiTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task StartStage_ReportsTheAgentAsUnavailable_NotAnInternalError()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/features/{_factory.FeatureId}/start-stage", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task StartStage_TellsTheUserHowToFixIt()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/features/{_factory.FeatureId}/start-stage", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("opencode", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winget install opencode", body);
    }

    [Fact]
    public async Task StartStage_CarriesARequestReference_SoSupportCanFindTheLogEntry()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/features/{_factory.FeatureId}/start-stage", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("reference:", body);
    }

    [Fact]
    public async Task StartStage_DoesNotLeakAStackTraceOrAnExceptionTypeName()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/features/{_factory.FeatureId}/start-stage", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("   at ", body);
        Assert.DoesNotContain(nameof(AgentLaunchException), body);
        Assert.DoesNotContain("Win32Exception", body);
    }

    [Fact]
    public async Task TheSameAnswerComesFromEveryAgentBackedEndpoint()
    {
        // The DRY claim, proved rather than asserted. Before ApiErrorMapper each route decided for
        // itself which exceptions it knew, so a new failure class meant touching every handler.
        // Here the fault is injected one layer down (the engine), so no route-specific state is
        // needed to reach it, and all of them have to answer identically on their own.
        using var client = new AgentFaultFactory().CreateClient();
        var id = Guid.NewGuid();

        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"/api/features/{id}/start-stage", null),
            (HttpMethod.Post, $"/api/features/{id}/run-stage", null),
            (HttpMethod.Post, $"/api/features/{id}/run-gates", null),
            (HttpMethod.Post, $"/api/features/{id}/run-gates-and-repair", null),
            (HttpMethod.Post, $"/api/features/{id}/advance", null),
            (HttpMethod.Post, $"/api/features/{id}/send-message", new { text = "hello" }),
            (HttpMethod.Post, $"/api/features/{id}/push-back", new { targetStageName = "business-analyst" }),
            (HttpMethod.Post, $"/api/features/{id}/retry-stage", new { targetStageName = "business-analyst" }),
            (HttpMethod.Post, $"/api/features/{id}/retry-finalize", null),
            // switch-model is deliberately absent: it resolves StageModelSwitcher rather than
            // IWorkflowEngine, so this fault is not on its path, and with no such feature a 404 is
            // the right answer. Listing it here would have tested a different thing.
        };

        foreach (var (method, path, body) in calls)
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body);

            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"{method} {path} answered {(int)response.StatusCode} instead of 503: {text}");
            Assert.Contains("opencode", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ReadOnlyEndpoints_AreUnaffectedByTheLaunchFailure()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/releases?workspacePath={Uri.EscapeDataString(_factory.WorkspacePath)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    /// <summary>
    /// Fault injection one layer below the routes: the engine itself throws the launch failure, so
    /// every agent-backed endpoint reaches the failure without needing a feature or a stage in a
    /// particular state. What is under test is the mapping, not the workflow.
    /// </summary>
    public sealed class AgentFaultFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowEngine>();
                services.AddSingleton<IWorkflowEngine>(_ => throw new AgentLaunchException(
                    "could not start 'C:\\opencode.exe' (error 448: the path cannot be traversed " +
                    "because it contains an untrusted mount point)",
                    new System.ComponentModel.Win32Exception(448)));
            });
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "devteam-launchfail-" + Guid.NewGuid().ToString("N"));

        public string WorkspacePath => Path.Combine(_root, "workspace");

        public string DatabasePath => Path.Combine(_root, "devteam.db");

        /// <summary>
        /// A real release + feature + flow position, seeded straight into the database.
        ///
        /// <para>
        /// Seeding is direct rather than going through <c>POST /api/releases</c> because creating a
        /// release also initialises the agent — so on a machine where the agent cannot launch, the
        /// only way to reach <c>start-stage</c> is to have the feature already in place. It is also
        /// the shape <c>start-stage</c> needs: it resolves the feature and its flow position before
        /// it touches the agent, so a made-up id would answer 404 and never reach the launch at all.
        /// </para>
        /// </summary>
        public Guid FeatureId { get; private set; }

        public Factory()
        {
            Directory.CreateDirectory(WorkspacePath);
            using var db = new DevTeamDbContext(new DbContextOptionsBuilder<DevTeamDbContext>()
                .UseSqlite($"Data Source={DatabasePath}")
                .Options);
            db.Database.EnsureCreated();

            var release = new DevTeamRelease
            {
                WorkspacePath = WorkspacePath,
                Title = "Launch failure probe",
                BranchName = "main",
                Status = ReleaseStatus.InProgress,
            };
            var feature = new ReleaseFeature
            {
                Release = release,
                Key = "launch-probe",
                Title = "Launch failure probe",
                BranchName = "feature/launch-probe",
                Status = ReleaseFeatureStatus.InProgress,
            };
            feature.FlowPosition = new ReleaseFlowPosition
            {
                Feature = feature,
                CurrentStageIndex = 0,
                CurrentStageName = "business-analyst",
            };
            release.Features.Add(feature);

            // ReleaseFeature's own initializer assigns Id = Guid.NewGuid(), and for a store-generated
            // key EF reads a non-default value as "this row already exists" — so the graph is tracked
            // as Unchanged and SaveChanges inserts nothing, without error. Forcing the state makes
            // the insert happen; the generated key lands on the instance either way.
            db.ReleaseFeatures.Add(feature);
            db.Entry(feature).State = EntityState.Added;
            db.SaveChanges();
            FeatureId = feature.Id;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DataDirectory", _root);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<DevTeamDbContext>>();
                services.AddDbContextFactory<DevTeamDbContext>(options =>
                    options.UseSqlite($"Data Source={DatabasePath}"));

                // The CLI is present but Windows refuses to start it — the real shape of the wild
                // failure, now expressed as the exception the launcher raises once every candidate
                // has been tried.
                services.RemoveAll<IAcpProcess>();
                services.AddSingleton<IAcpProcess>(_ => throw new AgentLaunchException(
                    "could not start 'C:\\opencode.exe' (error 448: the path cannot be traversed " +
                    "because it contains an untrusted mount point)",
                    new System.ComponentModel.Win32Exception(448)));

                // IAgentSpoke is deliberately NOT replaced: the production registration stays, so
                // this exercises the real wiring rather than a test double.
            });
        }

        public void Dispose()
        {
            base.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
