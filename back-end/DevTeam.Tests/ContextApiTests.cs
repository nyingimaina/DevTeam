using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Domain;
using DevTeam.Broker.SemaNami;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DevTeam.Tests;

/// <summary>
/// The context meter needs a number it can trust. These tests pin what the API says about the
/// context, and pin the wording of the compact command, because both are the difference between a
/// bar that reflects reality and one that flatters it.
/// </summary>
public class ContextApiTests : IClassFixture<ContextApiTests.AppFactory>
{
    private readonly AppFactory _factory;

    public ContextApiTests(AppFactory factory) => _factory = factory;

    /// <summary>
    /// The raw <c>WebApplicationFactory&lt;Program&gt;</c> fixture version of this class booted
    /// Program with the production connection string, and its boot-time recovery pass escalated
    /// the live app's mid-prompt runs. Every server fixture re-binds the database to its own
    /// file — this one does too now.
    /// </summary>
    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        public string DatabasePath { get; } =
            Path.Combine(Path.GetTempPath(), "devteam-context-" + Guid.NewGuid().ToString("N") + ".db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DataDirectory", Path.GetDirectoryName(DatabasePath) ?? ".");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(d =>
                    d.ServiceType == typeof(IDbContextFactory<DevTeamDbContext>));
                services.Remove(descriptor);
                services.AddDbContextFactory<DevTeamDbContext>(options =>
                    options.UseSqlite($"Data Source={DatabasePath}"));

                var listener = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(SemaNamiListenerService));
                if (listener is not null)
                    services.Remove(listener);
            });
        }
    }

    [Fact]
    public async Task CurrentContext_ReportsNoContextBeforeTheAgentHasSaidAnything()
    {
        // The honest answer before any turn has run is "I don't know", not zero. A bar that starts
        // at 0% looks like a measured reading of an empty context, which is a different claim.
        using var client = _factory.CreateClient();

        var context = await client.GetFromJsonAsync<JsonElement>("/api/context/current");

        Assert.Equal(0, context.GetProperty("usedTokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("contextSize").ValueKind);
        Assert.Equal(JsonValueKind.Null, context.GetProperty("updatedAt").ValueKind);
        Assert.False(context.GetProperty("turnActive").GetBoolean());
    }

    [Fact]
    public void TheContextReadingTravelsAsItsOwnEventRatherThanBorrowingTheUsageFields()
    {
        // How full the context is and how many tokens a turn produced are different numbers, and
        // they used to share one event with the window size crammed into the output-token field. A
        // client reading outputTokens as "context usage" was reading the wrong number, with nothing
        // in the payload to tell it so.
        Assert.Equal("contextChanged", BrokerCoordinator.EventContextChanged);
        Assert.NotEqual(BrokerCoordinator.EventUsageUpdated, BrokerCoordinator.EventContextChanged);
    }

    [Fact]
    public void TheCompactCommandAsksTheAgentToSummarizeRatherThanThrowingTheSessionAway()
    {
        // The broker's own reset is "discard the session and start over", which loses anything the
        // agent had worked out. Compacting asks the agent to summarize instead. The two are not
        // interchangeable and must not be spelled the same way.
        Assert.Equal("/compact", FeatureContextBoundary.CompactPromptText);
    }

    [Fact]
    public void CompactingAnUntrackedSessionIsRefusedRatherThanPromised()
    {
        // The endpoint needs a session whose context it actually knows about. Returning a fabricated
        // "compacted, here are the before and after numbers" for a session that was never measured
        // would be worse than a 404.
        var coordinator = _factory.Services.GetRequiredService<BrokerCoordinator>();

        var result = coordinator.CompactContextAsync(Guid.NewGuid(), CancellationToken.None).Result;

        Assert.Null(result);
    }
}
