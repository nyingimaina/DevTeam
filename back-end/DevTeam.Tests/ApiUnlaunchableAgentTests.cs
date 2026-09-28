using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Rpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevTeam.Tests;

/// <summary>
/// The in-the-wild failure this file pins down.
///
/// <c>GET /api/releases</c> returned <c>500</c> on a machine where opencode was installed and
/// working. The endpoint only reads the release list, but resolving <c>IWorkflowEngine</c> builds
/// the agent spoke, which builds <c>IAcpProcess</c>, whose factory <em>launches</em>
/// <c>opencode acp</c>. On the affected machine the launch failed (winget installs the CLI behind a
/// symbolic link, which <c>CreateProcess</c> refuses to traverse), the factory threw, and the
/// exception escaped through DI into an unrelated read endpoint.
///
/// So the invariant under test: reading releases must never depend on the agent being launchable.
/// The agent process has to be created on first agent use, not on first resolution.
/// </summary>
public sealed class ApiUnlaunchableAgentTests : IClassFixture<ApiUnlaunchableAgentTests.Factory>
{
    private readonly Factory _factory;

    public ApiUnlaunchableAgentTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task ListReleases_Succeeds_WhenTheAgentProcessCannotBeLaunched()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/releases?workspacePath={Uri.EscapeDataString(_factory.WorkspacePath)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task ListReleases_KeepsSucceeding_OnRepeatedReads()
    {
        using var client = _factory.CreateClient();

        // The UI polls this route, so a failure here was not a one-off: every poll returned 500.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await client.GetAsync(
                $"/api/releases?workspacePath={Uri.EscapeDataString(_factory.WorkspacePath)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ListHotfixes_Succeeds_WhenTheAgentProcessCannotBeLaunched()
    {
        using var client = _factory.CreateClient();

        // A second read-only route on the same engine (it 500'd alongside /api/releases in the
        // wild), to show the fix is not endpoint-specific.
        var response = await client.GetAsync(
            $"/api/hotfixes?workspacePath={Uri.EscapeDataString(_factory.WorkspacePath)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "devteam-unlaunchable-" + Guid.NewGuid().ToString("N"));

        public string WorkspacePath => Path.Combine(_root, "workspace");

        public string DatabasePath => Path.Combine(_root, "devteam.db");

        public Factory()
        {
            Directory.CreateDirectory(WorkspacePath);
            using var db = new DevTeamDbContext(new DbContextOptionsBuilder<DevTeamDbContext>()
                .UseSqlite($"Data Source={DatabasePath}")
                .Options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DataDirectory", _root);
            builder.ConfigureServices(services =>
            {
                // Own database, so the test never touches the real ~/.devteam/devteam.db.
                services.RemoveAll<IDbContextFactory<DevTeamDbContext>>();
                services.AddDbContextFactory<DevTeamDbContext>(options =>
                    options.UseSqlite($"Data Source={DatabasePath}"));

                // The failure being reproduced: the agent process cannot be launched. IAcpProcess
                // is left registered under its real service type and its factory always throws,
                // exactly as it did when the winget link could not be started.
                services.RemoveAll<IAcpProcess>();
                services.AddSingleton<IAcpProcess>(_ =>
                    throw new InvalidOperationException(
                        "An error occurred trying to start process 'opencode.exe'. " +
                        "The path cannot be traversed because it contains an untrusted mount point."));

                // IAgentSpoke is deliberately NOT replaced: the production registration stays, so
                // this exercises the real wiring rather than a test double.
            });
        }

        public void Dispose()
        {
            base.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
