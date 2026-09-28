using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using DevTeam.Tests.Rpc;

namespace DevTeam.Tests;

/// <summary>
/// Auto-compaction spends a model call, so the tests here are mostly about when it must NOT happen:
/// on a turn in flight, on a context nobody measured, and twice in a row without a refill. The
/// positive case matters too — a policy that never fires is just as broken as one that always does.
/// </summary>
public class AutoCompactionBoundaryTests : IDisposable
{
    private readonly SqliteConnectionShim _connection = new();
    private readonly AcpPipeHarness _harness = new();
    private readonly RecordingBroadcaster _broadcaster = new();

    public void Dispose()
    {
        _harness.Dispose();
        _connection.Dispose();
    }

    private BrokerCoordinator CreateCoordinator()
    {
        var spoke = new OpencodeAcpSpoke(_harness.Process);
        return new BrokerCoordinator(
            spoke,
            new TestDbContextFactory(_connection.Connection),
            _broadcaster,
            NullLogger<BrokerCoordinator>.Instance,
            new ActiveTurnTracker(),
            stallAfter: TimeSpan.FromMinutes(30));
    }

    private async Task<Guid> StartSessionAsync(BrokerCoordinator coordinator)
    {
        var pending = coordinator.NewSessionAsync(@"C:\work\proj", null, null, CancellationToken.None);
        var (_, initId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(initId, InitJson);
        var (_, newId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(newId, NewSessionJson);
        return (await pending).SessionId;
    }

    /// <summary>Runs one turn, optionally reporting a context, and returns the tokens it ended on.</summary>
    private async Task RunTurnAsync(BrokerCoordinator coordinator, Guid sessionId, long? used)
    {
        var pending = coordinator.PromptWithSessionRecoveryAsync(sessionId, "step", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        if (used is { } u)
        {
            _harness.EmitSessionUpdate("ses_abc",
                $"{{\"sessionUpdate\":\"usage_update\",\"used\":{u},\"size\":200000}}");
        }

        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");
        await pending;
    }

    [Fact]
    public async Task AFullContextIsCompactedAutomaticallyBeforeTheNextStep()
    {
        await using var coordinator = CreateCoordinator();
        var sessionId = await StartSessionAsync(coordinator);
        await RunTurnAsync(coordinator, sessionId, 190_000);

        var prompt = coordinator.PromptForFeatureWithAutoCompactAsync(
            sessionId, "feature-a", "next step", CancellationToken.None);

        // The first frame is the compaction itself, not the step the caller asked for.
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", method);

        // Then the compaction reports a much smaller context...
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":30000,\"size\":200000}");
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");

        // ...and only then does the step the caller actually asked for run.
        var (_, stepId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(stepId, "{\"stopReason\":\"end_turn\"}");
        await prompt;
    }

    [Fact]
    public async Task AContextUnderTheThresholdIsNotCompactedAutomatically()
    {
        await using var coordinator = CreateCoordinator();
        var sessionId = await StartSessionAsync(coordinator);
        await RunTurnAsync(coordinator, sessionId, 40_000);

        var prompt = coordinator.PromptForFeatureWithAutoCompactAsync(
            sessionId, "feature-a", "next step", CancellationToken.None);

        // No compaction frame: the very first request is the step itself.
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", method);
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");
        await prompt;

        Assert.Equal(1, _broadcaster.Events.Count(e => e.Type == BrokerCoordinator.EventContextChanged));
    }

    [Fact]
    public async Task AContextThatStayedFullIsNotCompactedAgainOnTheVeryNextStep()
    {
        // The loop this prevents. A compaction that frees little leaves the context over the line,
        // and without a refill requirement every following step would pay for another one.
        await using var coordinator = CreateCoordinator();
        var sessionId = await StartSessionAsync(coordinator);
        await RunTurnAsync(coordinator, sessionId, 190_000);

        var first = coordinator.PromptForFeatureWithAutoCompactAsync(
            sessionId, "feature-a", "step one", CancellationToken.None);
        var (_, compactId, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":185000,\"size\":200000}");
        _harness.Reply(compactId, "{\"stopReason\":\"end_turn\"}");
        var (_, stepId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(stepId, "{\"stopReason\":\"end_turn\"}");
        await first;

        var second = coordinator.PromptForFeatureWithAutoCompactAsync(
            sessionId, "feature-a", "step two", CancellationToken.None);

        // No compaction this time: the first request is the step.
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", method);
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");
        await second;
    }

    [Fact]
    public async Task AnUnmeasuredContextIsNotCompactedOnAGuess()
    {
        await using var coordinator = CreateCoordinator();
        var sessionId = await StartSessionAsync(coordinator);
        await RunTurnAsync(coordinator, sessionId, used: null);

        var prompt = coordinator.PromptForFeatureWithAutoCompactAsync(
            sessionId, "feature-a", "next step", CancellationToken.None);

        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", method);
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");
        await prompt;
    }

    [Fact]
    public async Task TheCompactionResultIsRecordedSoTheCurveShowsTheDrop()
    {
        // A compaction that is not written down is indistinguishable from one that did not happen.
        await using var coordinator = CreateCoordinator();
        var sessionId = await StartSessionAsync(coordinator);
        await RunTurnAsync(coordinator, sessionId, 190_000);

        var prompt = coordinator.PromptForFeatureWithAutoCompactAsync(
            sessionId, "feature-a", "next step", CancellationToken.None);
        var (_, compactId, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":30000,\"size\":200000}");
        _harness.Reply(compactId, "{\"stopReason\":\"end_turn\"}");
        var (_, stepId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(stepId, "{\"stopReason\":\"end_turn\"}");
        await prompt;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<ContextUsageSample> samples;
        do
        {
            await using var db = new TestDbContextFactory(_connection.Connection).CreateDbContext();
            samples = db.ContextUsageSamples.Where(s => s.SessionId == sessionId).OrderBy(s => s.Id).ToList();
            if (samples.Any(s => s.Source == ContextSampleSource.Compaction)) break;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);

        var compaction = Assert.Single(samples, s => s.Source == ContextSampleSource.Compaction);
        Assert.Equal(0, compaction.UsedTokens);
    }

    private const string InitJson =
        "{\"protocolVersion\":1,\"agentCapabilities\":{\"loadSession\":false}}";

    private const string NewSessionJson =
        "{\"sessionId\":\"ses_abc\",\"configOptions\":[]}";

    private sealed class SqliteConnectionShim : IDisposable
    {
        public SqliteConnection Connection { get; }

        public SqliteConnectionShim()
        {
            Connection = new SqliteConnection("Data Source=:memory:");
            Connection.Open();
            using var db = new TestDbContextFactory(Connection).CreateDbContext();
            db.Database.EnsureCreated();
        }

        public void Dispose() => Connection.Dispose();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<DevTeamDbContext>
    {
        private readonly SqliteConnection _connection;

        public TestDbContextFactory(SqliteConnection connection) => _connection = connection;

        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(_connection).Options);
    }

    private sealed class RecordingBroadcaster : IEventBroadcaster
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
