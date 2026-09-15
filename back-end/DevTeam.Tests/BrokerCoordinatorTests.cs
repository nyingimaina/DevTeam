using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using DevTeam.Tests.Rpc;

namespace DevTeam.Tests;

public class BrokerCoordinatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcpPipeHarness _harness = new();
    private readonly RecordingBroadcaster _broadcaster = new();

    public BrokerCoordinatorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _harness.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetAgentInfo_ReturnsAgentDetails()
    {
        await using var coordinator = CreateCoordinator();
        var pending = coordinator.GetAgentInfoAsync(CancellationToken.None);
        var (initMethod, initId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", initMethod);
        _harness.Reply(initId, InitializeResultJson);

        var info = await pending;
        Assert.Equal("TestAgent", info.AgentName);
        Assert.Equal("1.0.0", info.AgentVersion);
    }

    [Fact]
    public async Task GetAgentInfo_ThenNewSession_OnlyInitializesOnce()
    {
        await using var coordinator = CreateCoordinator();

        var infoPending = coordinator.GetAgentInfoAsync(CancellationToken.None);
        var (initMethod, initId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", initMethod);
        _harness.Reply(initId, InitializeResultJson);
        await infoPending;

        var sessionPending = coordinator.NewSessionAsync(@"C:\work\proj", null, null, CancellationToken.None);
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", method);
        _harness.Reply(idRaw, NewSessionResultJson);
        await sessionPending;
    }

    [Fact]
    public async Task NewSession_StoresSessionAndModelOptions()
    {
        await using var coordinator = CreateCoordinator();
        var pending = coordinator.NewSessionAsync(@"C:\work\proj", null, null, CancellationToken.None);
        var (initMethod, initId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", initMethod);
        _harness.Reply(initId, InitializeResultJson);
        var (method, idRaw, _) = await _harness.ReadRequestAsync();

        Assert.Equal("session/new", method);
        _harness.Reply(idRaw, NewSessionResultJson);

        var session = await pending;
        Assert.Equal("ses_abc", session.AcpSessionId);
        Assert.Equal(@"C:\work\proj", session.WorkspacePath);
        Assert.Equal("opencode/big-pickle", session.ModelId);
        Assert.Single(session.Models);
        Assert.Equal("opencode/big-pickle", session.Models[0].Value);
        Assert.Equal("build", session.ModeId);
        Assert.Equal(2, session.Modes.Count);
        Assert.Contains(session.Modes, m => m.Value == "plan");
    }

    [Fact]
    public async Task PromptWithSessionRecovery_StreamsEventsAndPersistsMessages()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (method, idRaw, _) = await _harness.ReadRequestAsync();

        Assert.Equal("session/prompt", method);
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"messageId\":\"msg_1\",\"content\":{\"type\":\"text\",\"text\":\"Hel\"}}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"messageId\":\"msg_1\",\"content\":{\"type\":\"text\",\"text\":\"lo\"}}");
        _harness.Reply(idRaw,
            "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":100,\"outputTokens\":2,\"totalTokens\":102}}");

        var result = await pending;
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(102, result.TotalTokens);

        var textDeltas = _broadcaster.Events.Where(e => e.Type == BrokerCoordinator.EventTextDelta).ToList();
        Assert.Equal(2, textDeltas.Count);
        Assert.Equal("msg_1", textDeltas[0].Payload!.Value.GetProperty("messageId").GetString());
        Assert.Equal("Hel", textDeltas[0].Payload!.Value.GetProperty("text").GetString());
        Assert.Contains(_broadcaster.Events, e => e.Type == BrokerCoordinator.EventTurnEnd);

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Messages.Count);
        var user = detail.Messages.Single(m => m.Role == "user");
        Assert.Equal("hello", user.BodyText);
        var assistant = detail.Messages.Single(m => m.Role == "assistant");
        Assert.Equal("Hello", assistant.BodyText);
        Assert.Equal("msg_1", assistant.AcpMessageId);
    }

    [Fact]
    public async Task PromptWithSessionRecovery_OnUnknownSession_Throws()
    {
        await using var coordinator = CreateCoordinator();
        var pending = coordinator.PromptWithSessionRecoveryAsync(Guid.NewGuid(), "hi", CancellationToken.None);
        var (initMethod, initId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", initMethod);
        _harness.Reply(initId, InitializeResultJson);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => pending);
    }

    [Fact]
    public async Task PromptWithSessionRecovery_SerializesConcurrentTurns()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var first = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "first", CancellationToken.None);
        var (firstMethod, firstIdRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", firstMethod);

        var secondTask = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "second", CancellationToken.None);
        await Task.Delay(200);
        Assert.False(secondTask.IsCompleted);

        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"messageId\":\"m1\",\"content\":{\"type\":\"text\",\"text\":\"one\"}}");
        _harness.Reply(firstIdRaw,
            "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":10,\"outputTokens\":1,\"totalTokens\":11}}");
        await first;

        var (secondMethod, secondIdRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", secondMethod);
        _harness.Reply(secondIdRaw,
            "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":10,\"outputTokens\":1,\"totalTokens\":11}}");
        await secondTask;

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal(4, detail!.Messages.Count);
    }

    [Fact]
    public async Task PromptWithSessionRecovery_WhenAcpSessionNotRecognized_RecreatesSessionAndRetries()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);

        var (firstMethod, firstId, firstParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", firstMethod);
        using (var doc = JsonDocument.Parse(firstParams))
            Assert.Equal("ses_abc", doc.RootElement.GetProperty("sessionId").GetString());
        _harness.ReplyError(firstId, -32602, "Invalid params: session not found: ses_abc");

        var (newSessionMethod, newSessionId, newSessionParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        using (var doc = JsonDocument.Parse(newSessionParams))
            Assert.Equal(@"C:\work\proj", doc.RootElement.GetProperty("cwd").GetString());
        _harness.Reply(newSessionId, "{\"sessionId\":\"ses_def\",\"configOptions\":[]}");

        var (retryMethod, retryId, retryParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", retryMethod);
        using (var doc = JsonDocument.Parse(retryParams))
            Assert.Equal("ses_def", doc.RootElement.GetProperty("sessionId").GetString());
        _harness.Reply(retryId,
            "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":10,\"outputTokens\":2,\"totalTokens\":12}}");

        var result = await pending;
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(12, result.TotalTokens);

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("ses_def", detail!.AcpSessionId);
        var userMessages = detail.Messages.Where(m => m.Role == "user").ToList();
        Assert.Single(userMessages);
        Assert.Equal("hello", userMessages[0].BodyText);
    }

    [Fact]
    public async Task PromptWithSessionRecovery_WhileRunning_IsVisibleAsTheActiveTurn()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello there", CancellationToken.None);
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", method);

        Assert.NotNull(_turnTracker.Current);
        Assert.Equal(session.SessionId, _turnTracker.Current!.SessionId);
        Assert.Equal("hello there", _turnTracker.Current.Preview);

        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":10,\"outputTokens\":1,\"totalTokens\":11}}");
        await pending;

        Assert.Null(_turnTracker.Current);
    }

    [Fact]
    public async Task CancelCurrentTurn_CancelsTheInFlightPrompt_AndFreesTheLockForTheNextCaller()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var stuck = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "stuck forever", CancellationToken.None);
        await _harness.ReadRequestAsync();
        Assert.NotNull(_turnTracker.Current);

        var cancelled = await coordinator.CancelCurrentTurnAsync(CancellationToken.None);

        Assert.True(cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck);
        Assert.Null(_turnTracker.Current);

        // CancelCurrentTurnAsync also best-effort notifies the agent — drain that
        // session/cancel notification before looking for the next request.
        var (cancelMethod, _, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/cancel", cancelMethod);

        // The lock is free again: a new prompt can proceed.
        var next = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "next one", CancellationToken.None);
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", method);
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":5,\"outputTokens\":1,\"totalTokens\":6}}");
        await next;
    }

    [Fact]
    public async Task CancelCurrentTurn_WithNothingRunning_ReturnsFalse()
    {
        await using var coordinator = CreateCoordinator();
        var cancelled = await coordinator.CancelCurrentTurnAsync(CancellationToken.None);
        Assert.False(cancelled);
    }

    [Fact]
    public async Task SetModel_UpdatesStoreAndSpoke()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.SetModelAsync(session.SessionId, "opencode/big-pickle-v2", CancellationToken.None);
        var (method, idRaw, paramsJson) = await _harness.ReadRequestAsync();

        Assert.Equal("session/set_model", method);
        using (var doc = JsonDocument.Parse(paramsJson))
        {
            Assert.Equal("ses_abc", doc.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("opencode/big-pickle-v2", doc.RootElement.GetProperty("modelId").GetString());
        }

        _harness.Reply(idRaw, "{}");
        await pending;

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("opencode/big-pickle-v2", detail!.ModelId);
        Assert.Empty(_broadcaster.Events);
    }

    [Fact]
    public async Task SetModel_WhenAcpSessionNotRecognized_RecreatesSessionAndRetries()
    {
        // Same scenario as PromptWithSessionRecovery: the broker's opencode process
        // restarted after this session row was created, so the agent no longer
        // recognizes the stored AcpSessionId.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.SetModelAsync(session.SessionId, "opencode/big-pickle", CancellationToken.None);

        var (firstMethod, firstId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", firstMethod);
        _harness.ReplyError(firstId, -32602, "Invalid params: session not found: ses_abc");

        var (newSessionMethod, newSessionId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        _harness.Reply(newSessionId, "{\"sessionId\":\"ses_def\",\"configOptions\":[]}");

        var (retryMethod, retryId, retryParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", retryMethod);
        using (var doc = JsonDocument.Parse(retryParams))
            Assert.Equal("ses_def", doc.RootElement.GetProperty("sessionId").GetString());
        _harness.Reply(retryId, "{}");

        var result = await pending;
        Assert.Equal("opencode/big-pickle", result);

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("ses_def", detail!.AcpSessionId);
        Assert.Equal("opencode/big-pickle", detail.ModelId);
    }

    [Fact]
    public async Task SetMode_WhenAcpSessionNotRecognized_RecreatesSessionAndRetries()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.SetModeAsync(session.SessionId, "plan", CancellationToken.None);

        var (firstMethod, firstId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_mode", firstMethod);
        _harness.ReplyError(firstId, -32602, "Invalid params: session not found: ses_abc");

        var (newSessionMethod, newSessionId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        _harness.Reply(newSessionId, "{\"sessionId\":\"ses_def\",\"configOptions\":[]}");

        var (retryMethod, retryId, retryParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_mode", retryMethod);
        using (var doc = JsonDocument.Parse(retryParams))
            Assert.Equal("ses_def", doc.RootElement.GetProperty("sessionId").GetString());
        _harness.Reply(retryId, "{}");

        var result = await pending;
        Assert.Equal("plan", result);

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("ses_def", detail!.AcpSessionId);
        Assert.Equal("plan", detail.ModeId);
    }

    [Fact]
    public async Task SetMode_UpdatesStoreAndSpoke()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.SetModeAsync(session.SessionId, "plan", CancellationToken.None);
        var (method, idRaw, paramsJson) = await _harness.ReadRequestAsync();

        Assert.Equal("session/set_mode", method);
        using (var doc = JsonDocument.Parse(paramsJson))
        {
            Assert.Equal("ses_abc", doc.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("plan", doc.RootElement.GetProperty("modeId").GetString());
        }

        _harness.Reply(idRaw, "{}");
        await pending;

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("plan", detail!.ModeId);
        Assert.Empty(_broadcaster.Events);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private readonly ActiveTurnTracker _turnTracker = new();

    private BrokerCoordinator CreateCoordinator()
    {
        var spoke = new OpencodeAcpSpoke(_harness.Process);
        return new BrokerCoordinator(spoke, CreateFactory(), _broadcaster, NullLogger<BrokerCoordinator>.Instance, _turnTracker);
    }

    private IDbContextFactory<DevTeamDbContext> CreateFactory()
        => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory : IDbContextFactory<DevTeamDbContext>
    {
        private readonly SqliteConnection _connection;

        public TestDbContextFactory(SqliteConnection connection) => _connection = connection;

        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(_connection).Options);
    }

    private async Task<SessionSummary> CreateSessionAsync(BrokerCoordinator coordinator)
    {
        var pending = coordinator.NewSessionAsync(@"C:\work\proj", null, null, CancellationToken.None);
        var (initMethod, initId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", initMethod);
        _harness.Reply(initId, InitializeResultJson);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.Reply(idRaw, NewSessionResultJson);
        return await pending;
    }

    private const string InitializeResultJson =
        "{\"protocolVersion\":1,"
        + "\"agentInfo\":{\"name\":\"TestAgent\",\"version\":\"1.0.0\"}}";

    private const string NewSessionResultJson =
        "{\"sessionId\":\"ses_abc\",\"configOptions\":["
        + "{\"id\":\"model\",\"name\":\"Model\",\"category\":\"model\",\"type\":\"select\","
        + "\"currentValue\":\"opencode/big-pickle\","
        + "\"options\":[{\"value\":\"opencode/big-pickle\",\"name\":\"OpenCode Big Pickle\"}]},"
        + "{\"id\":\"mode\",\"name\":\"Mode\",\"category\":\"provider\",\"type\":\"select\","
        + "\"currentValue\":\"build\","
        + "\"options\":[{\"value\":\"build\",\"name\":\"Build\"},{\"value\":\"plan\",\"name\":\"Plan\"}]}]}";
}

internal sealed class RecordingBroadcaster : IEventBroadcaster
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