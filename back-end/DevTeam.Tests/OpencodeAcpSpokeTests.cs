using System.Text.Json;
using DevTeam.Broker.Spoke;
using DevTeam.Tests.Rpc;

namespace DevTeam.Tests;

public class OpencodeAcpSpokeTests : IDisposable
{
    private readonly AcpPipeHarness _harness = new();

    public void Dispose()
    {
        _harness.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_ReturnsAgentInfo()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);

        var pending = spoke.InitializeAsync(CancellationToken.None);
        var (method, idRaw, paramsJson) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", method);
        using (var doc = JsonDocument.Parse(paramsJson))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("protocolVersion").GetInt32());
        }

        _harness.Reply(idRaw, "{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"OpenCode\",\"version\":\"1.18.14\"}}");

        var info = await pending;
        Assert.Equal("OpenCode", info.Name);
        Assert.Equal("1.18.14", info.Version);
    }

    [Fact]
    public async Task NewSessionAsync_ReturnsSessionIdAndModelOptions()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);

        var pending = spoke.NewSessionAsync("C:\\work\\proj", CancellationToken.None);
        var (method, idRaw, paramsJson) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", method);
        using (var doc = JsonDocument.Parse(paramsJson))
        {
            Assert.Equal("C:\\work\\proj", doc.RootElement.GetProperty("cwd").GetString());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("mcpServers").ValueKind);
        }

        _harness.Reply(idRaw,
            "{\"sessionId\":\"ses_abc\",\"configOptions\":["
            + "{\"id\":\"model\",\"name\":\"Model\",\"category\":\"model\",\"type\":\"select\","
            + "\"currentValue\":\"opencode/big-pickle\","
            + "\"options\":[{\"value\":\"opencode/big-pickle\",\"name\":\"OpenCode Big Pickle\"}]}]}");

        var session = await pending;
        Assert.Equal("ses_abc", session.SessionId);
        var model = Assert.Single(session.ConfigOptions, o => o.Id == "model");
        Assert.Equal("opencode/big-pickle", model.CurrentValue);
        Assert.Contains(model.Options, o => o.Value == "opencode/big-pickle");
    }

    [Fact]
    public async Task PromptAsync_StreamsTextAndReturnsFinalResult()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);
        var received = new List<AgentEvent>();
        spoke.EventReceived += (_, e) => received.Add(e);

        var pending = spoke.PromptAsync("ses_abc", [new AgentPromptPart("text", "hello")], CancellationToken.None);
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", method);

        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"messageId\":\"msg_1\",\"content\":{\"type\":\"text\",\"text\":\"Hel\"}}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"messageId\":\"msg_1\",\"content\":{\"type\":\"text\",\"text\":\"lo\"}}");

        _harness.Reply(idRaw,
            "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":100,\"outputTokens\":2,\"totalTokens\":102,\"cachedReadTokens\":1},\"_meta\":{}}");

        var result = await pending;
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(100, result.Usage!.InputTokens);
        Assert.Equal(102, result.Usage.TotalTokens);

        var deltas = received.OfType<AgentTextDelta>().ToList();
        Assert.Equal(2, deltas.Count);
        Assert.Equal("Hel", deltas[0].Text);
        Assert.Equal("lo", deltas[1].Text);
    }

    [Fact]
    public async Task PromptAsync_ThoughtsToolCallsUsageAndConfig_AreRaised()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);
        var received = new List<AgentEvent>();
        spoke.EventReceived += (_, e) => received.Add(e);

        var pending = spoke.PromptAsync("ses_abc", [new AgentPromptPart("text", "run tool")], CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();

        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_thought_chunk\",\"messageId\":\"part_1\",\"content\":{\"type\":\"text\",\"text\":\"thinking\"}}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call_1\",\"title\":\"bash ls\",\"status\":\"pending\",\"kind\":\"execute\",\"rawInput\":{\"command\":\"ls\"}}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call_1\",\"status\":\"completed\",\"kind\":\"execute\",\"rawOutput\":{\"out\":\"ok\"}}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":42,\"size\":8192,\"cost\":{\"amount\":1.23,\"currency\":\"USD\"}}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"config_option_update\",\"configOptions\":[{\"id\":\"model\",\"currentValue\":\"opencode/big-pickle\"}]}");

        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\",\"_meta\":{}}");
        await pending;

        Assert.Single(received.OfType<AgentThoughtDelta>());

        var toolCall = Assert.Single(received.OfType<AgentToolCallEvent>());
        Assert.Equal("call_1", toolCall.Call.ToolCallId);
        Assert.Equal("execute", toolCall.Call.Kind);
        Assert.Equal("bash ls", toolCall.Call.Title);

        var update = Assert.Single(received.OfType<AgentToolCallUpdatedEvent>());
        Assert.Equal("completed", update.Call.Status);

        var usage = Assert.Single(received.OfType<AgentUsageUpdatedEvent>());
        Assert.Equal(42, usage.Usage.UsedTokens);
        Assert.Equal(1.23m, usage.Usage.CostAmount);

        var config = Assert.Single(received.OfType<AgentConfigOptionsUpdatedEvent>());
        Assert.Equal("opencode/big-pickle", config.Options[0].CurrentValue);
    }

    [Fact]
    public async Task SetModelAsync_SendsSetModelRequest()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);

        var pending = spoke.SetModelAsync("ses_abc", "opencode/big-pickle", CancellationToken.None);
        var (method, idRaw, paramsJson) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", method);
        using (var doc = JsonDocument.Parse(paramsJson))
        {
            Assert.Equal("ses_abc", doc.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("opencode/big-pickle", doc.RootElement.GetProperty("modelId").GetString());
        }

        _harness.Reply(idRaw, "{}");
        await pending;
    }

    [Fact]
    public async Task SetModeAsync_SendsSetModeRequest()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);

        var pending = spoke.SetModeAsync("ses_abc", "build", CancellationToken.None);
        var (method, idRaw, paramsJson) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_mode", method);
        using (var doc = JsonDocument.Parse(paramsJson))
        {
            Assert.Equal("ses_abc", doc.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("build", doc.RootElement.GetProperty("modeId").GetString());
        }

        _harness.Reply(idRaw, "{}");
        await pending;
    }

    [Fact]
    public async Task CancelAsync_SendsNotificationWithoutId()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);

        await spoke.CancelAsync("ses_abc", CancellationToken.None);

        var frame = await _harness.ReadClientFrameAsync();
        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("session/cancel", doc.RootElement.GetProperty("method").GetString());
        Assert.False(doc.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task RequestPermission_DefaultPolicy_RepliesCancelled()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process);

        _harness.Emit("{"
                      + "\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"session/request_permission\","
                      + "\"params\":{\"sessionId\":\"ses_abc\","
                      + "\"toolCall\":{\"toolCallId\":\"perm_1\",\"title\":\"Run ls\",\"kind\":\"execute\","
                      + "\"rawInput\":{\"command\":\"ls\"}},"
                      + "\"options\":[{\"optionId\":\"once\",\"name\":\"Allow once\",\"kind\":\"allow_once\"}]}}");

        var frame = await _harness.ReadClientFrameAsync();
        using var doc = JsonDocument.Parse(frame);
        Assert.Equal(5, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("cancelled",
            doc.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task RequestPermission_CustomPolicy_AllowOnce_RepliesSelected()
    {
        using var spoke = new OpencodeAcpSpoke(_harness.Process, new AlwaysAllowOncePolicy());

        _harness.Emit("{"
                      + "\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"session/request_permission\","
                      + "\"params\":{\"sessionId\":\"ses_abc\","
                      + "\"toolCall\":{\"toolCallId\":\"perm_2\",\"title\":\"Write file\",\"kind\":\"edit\"},\"options\":[]}}");

        var frame = await _harness.ReadClientFrameAsync();
        using var doc = JsonDocument.Parse(frame);
        var outcome = doc.RootElement.GetProperty("result").GetProperty("outcome");
        Assert.Equal("selected", outcome.GetProperty("outcome").GetString());
        Assert.Equal("once", outcome.GetProperty("optionId").GetString());
    }

    private sealed class AlwaysAllowOncePolicy : IPermissionPolicy
    {
        public Task<PermissionVerdict> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(PermissionVerdict.AllowOnce);
    }
}