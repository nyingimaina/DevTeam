using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Domain;
using DevTeam.Broker.SemaNami;
using DevTeam.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DevTeam.Tests.Licensing;

// /healthz used to answer only "ok + version" — indistinguishable between broker
// instances. The desktop shell needs to know WHICH broker answered before adopting it
// instead of spawning a second one (2026-10-01: adopt-vs-spawn was guesswork, and the
// guesses fed a nine-broker storm). The boot identity below is that answer.
public sealed class HealthzBootInfoTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "devteam-healthz-" + Guid.NewGuid().ToString("N") + ".db");

    private HealthzAppFactory? _factory;

    [Fact]
    public async Task Healthz_ReportsProcessId_BootId_AndDataDirFingerprint()
    {
        _factory = new HealthzAppFactory(_dbPath);
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.EnsureSuccessStatusCode();
        var health = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", health.GetProperty("status").GetString());

        // The factory-hosted host runs in this process, so the identity assertions are exact.
        Assert.Equal(Environment.ProcessId, health.GetProperty("processId").GetInt32());

        var bootId = health.GetProperty("bootId").GetString();
        Assert.True(Guid.TryParse(bootId, out _), $"bootId should be a guid, got '{bootId}'");

        var expectedDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".devteam");
        Assert.Equal(
            BrokerIdentity.DataDirFingerprint(expectedDataDir),
            health.GetProperty("dataDirHash").GetString());
    }

    [Fact]
    public void Healthz_BootId_IsStableForTheLifetimeOfOneHost()
    {
        _factory = new HealthzAppFactory(_dbPath);
        var client = _factory.CreateClient();

        var first = BootIdOf(client);
        var second = BootIdOf(client);

        Assert.Equal(first, second);
    }

    private static string BootIdOf(HttpClient client)
    {
        var health = client.GetFromJsonAsync<JsonElement>("/healthz").GetAwaiter().GetResult();
        return health.GetProperty("bootId").GetString()!;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        // Microsoft.Data.Sqlite pools handles, so a pooled open handle would otherwise keep the
        // file locked past the app's disposal and every cleanup would retry forever.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
                return;
            }
            catch when (i < 4) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(100); }
        }
    }

    /// <summary>
    /// Same shape as WorkflowCrashRecoveryStartupTests.RecoveryAppFactory: its own SQLite file
    /// so the boot path never touches the real devteam.db, and nothing reaching the network.
    /// </summary>
    private sealed class HealthzAppFactory(string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(d =>
                    d.ServiceType == typeof(IDbContextFactory<DevTeamDbContext>));
                services.Remove(descriptor);
                services.AddDbContextFactory<DevTeamDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));

                var listener = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(SemaNamiListenerService));
                if (listener is not null)
                    services.Remove(listener);
            });
        }
    }
}
