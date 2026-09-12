using System.Text.Json;
using DevTeam.Broker.Domain;
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
    public async Task NewSession_StoresSessionAndModelOptions()
    {
        await using var coordinator = CreateCoordinator();
        var pending = coordinator.NewSessionAsync(@"C:\work\proj", null, CancellationToken.None);
        var (method, idRaw, _) = await _harness.ReadRequestAsync();

        Assert.Equal("session/new", method);
        _harness.Reply(idRaw, NewSessionResultJson);

        var session = await pending;
        Assert.Equal("ses_abc", session.AcpSessionId);
        Assert.Equal(@"C:\work\proj", session.WorkspacePath);
        Assert.Equal("opencode/big-pickle", session.ModelId);
        Assert.Single(session.Models);
        Assert.Equal("opencode/big-pickle", session.Models[0].Value);
    }

    [Fact]
    public async Task Prompt_StreamsEventsAndPersistsMessages()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptAsync(session.SessionId, "hello", CancellationToken.None);
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
        Assert.Equal("msg_1", textDeltas[0].Payload!.Value.GetProperty("MessageId").GetString());
        Assert.Equal("Hel", textDeltas[0].Payload!.Value.GetProperty("Text").GetString());
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
    public async Task Prompt_OnUnknownSession_Throws()
    {
        await using var coordinator = CreateCoordinator();
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await coordinator.PromptAsync(Guid.NewGuid(), "hi", CancellationToken.None));
    }

    [Fact]
    public async Task Prompt_SerializesConcurrentTurns()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var first = coordinator.PromptAsync(session.SessionId, "first", CancellationToken.None);
        var (firstMethod, firstIdRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", firstMethod);

        var secondTask = coordinator.PromptAsync(session.SessionId, "second", CancellationToken.None);
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
    public async Task DeleteSession_RemovesRow()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var deleted = await coordinator.DeleteSessionAsync(session.SessionId, CancellationToken.None);
        Assert.True(deleted);

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Null(detail);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private BrokerCoordinator CreateCoordinator()
    {
        var spoke = new OpencodeAcpSpoke(_harness.Process);
        return new BrokerCoordinator(spoke, CreateFactory(), _broadcaster, NullLogger<BrokerCoordinator>.Instance);
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
        var pending = coordinator.NewSessionAsync(@"C:\work\proj", null, CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.Reply(idRaw, NewSessionResultJson);
        return await pending;
    }

    private const string NewSessionResultJson =
        "{\"sessionId\":\"ses_abc\",\"configOptions\":["
        + "{\"id\":\"model\",\"name\":\"Model\",\"category\":\"model\",\"type\":\"select\","
        + "\"currentValue\":\"opencode/big-pickle\","
        + "\"options\":[{\"value\":\"opencode/big-pickle\",\"name\":\"OpenCode Big Pickle\"}]}]}";
}

internal sealed class RecordingBroadcaster : IEventBroadcaster
{
    public List<StreamEvent> Events { get; } = [];

    public Task BroadcastAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
    {
        Events.Add(streamEvent);
        return Task.CompletedTask;
    }
}