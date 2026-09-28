using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Models;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
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

        await ReplyToReappliedModelAndModeAsync();

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
    public async Task SetModel_RemembersTheChoiceForTheWorkspace()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.SetModelAsync(session.SessionId, "anthropic/claude-x", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.Reply(idRaw, "{}");
        await pending;

        await using var db = CreateFactory().CreateDbContext();
        var settings = await db.WorkspaceModelSettings.SingleAsync();
        Assert.Equal("anthropic/claude-x", settings.ModelId);
        Assert.Equal(@"C:\work\proj", settings.WorkspacePath);
    }

    [Fact]
    public async Task SetModel_UpdatesTheRememberedChoice_OnASecondChange()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var first = coordinator.SetModelAsync(session.SessionId, "anthropic/claude-x", CancellationToken.None);
        var (_, firstId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(firstId, "{}");
        await first;

        var second = coordinator.SetModelAsync(session.SessionId, "openai/gpt-y", CancellationToken.None);
        var (_, secondId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(secondId, "{}");
        await second;

        await using var db = CreateFactory().CreateDbContext();
        Assert.Equal("openai/gpt-y", (await db.WorkspaceModelSettings.SingleAsync()).ModelId);
    }

    [Fact]
    public async Task Prompt_WhenTheAgentGoesSilent_IsTreatedAsStalled_InsteadOfWaitingForTheTimeout()
    {
        // Regression: a prompt the agent accepted but never answered produced no events at all
        // and waited out the full 30-minute budget — which the user experiences as a hang
        // (observed in the field: 22 minutes of silence, only ended by pressing Cancel).
        await using var coordinator = CreateCoordinator(stallAfter: TimeSpan.FromMilliseconds(250));
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "go", CancellationToken.None);
        await _harness.ReadRequestAsync(); // the agent never replies

        await Assert.ThrowsAsync<AcpStalledException>(() => pending);
        // The slot is freed, so the stage can be retried immediately.
        Assert.Null(_turnTracker.Current);
    }

    [Fact]
    public async Task Prompt_WhenTheAgentGoesSilent_ButAnotherModelIsAvailable_TriesIt()
    {
        // Live incident (2026-09-28, developer stage "create-installer"): the agent streamed for
        // ~45 seconds after the priming prompt and then went completely silent; the watchdog
        // escalated, the pipeline retried the SAME model, it went silent again, and the stage
        // never completed across three attempts. Silence behaves like a refusal: if a second
        // model is available, switch and retry within the turn instead of replaying a dead one.
        var candidates = new ModelCandidateService(CreateFactory());
        await using var coordinator = CreateCoordinator(
            stallAfter: TimeSpan.FromMilliseconds(250), modelCandidates: candidates);
        var session = await CreateSessionAsync(coordinator);
        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);

        await _harness.ReadRequestAsync(); // first attempt: the agent accepts and never emits another frame

        // The silent model is cooled down and the agent is pointed at the seeded second candidate.
        var (switchMethod, switchId, switchParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", switchMethod);
        Assert.Contains("opencode-go/kimi-k3", switchParams);
        _harness.Reply(switchId, "{}");

        // The retry goes out on the new model and is answered normally this time.
        var (retryMethod, retryId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", retryMethod);
        _harness.Reply(retryId, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":2,\"outputTokens\":1,\"totalTokens\":3}}");

        var result = await pending;
        Assert.Equal("end_turn", result.StopReason);

        // The silent model gets a cooldown, exactly like a refused one, so the next turn
        // does not attempt the dead model again.
        var after = await candidates.ListAsync(@"C:\work\proj", CancellationToken.None);
        Assert.NotNull(after[0].CooldownUntil);
        Assert.Equal("Unknown", after[0].LastFailureKind);
    }

    [Fact]
    public async Task Prompt_WhenTheAgentGoesSilent_WithEveryModelDead_StillEscalatesStalled()
    {
        // Without an alternative the old contract holds: AcpStalledException with the silent
        // duration, so the stage escalates with a reason the user can act on.
        var candidates = new ModelCandidateService(CreateFactory());
        foreach (var candidate in await candidates.ListSeededAsync(@"C:\work\proj", CancellationToken.None))
            await candidates.SetEnabledAsync(candidate.Id, false, CancellationToken.None);

        await using var coordinator = CreateCoordinator(
            stallAfter: TimeSpan.FromMilliseconds(250), modelCandidates: candidates);
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "go", CancellationToken.None);
        await _harness.ReadRequestAsync(); // the agent never replies

        await Assert.ThrowsAsync<AcpStalledException>(() => pending);
        Assert.Null(_turnTracker.Current);
    }

    [Fact]
    public async Task Prompt_WithOngoingActivity_IsNotTreatedAsStalled()
    {
        await using var coordinator = CreateCoordinator(stallAfter: TimeSpan.FromSeconds(1));
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "go", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();

        // Keep the heartbeat fresh for longer than the stall budget.
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(300);
            _harness.EmitSessionUpdate("ses_abc", "{\"sessionUpdate\":\"usage_update\",\"used\":1}");
        }

        Assert.False(pending.IsCompleted, "an active turn must not be killed as stalled");

        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":1,\"outputTokens\":1,\"totalTokens\":2}}");
        await pending;
    }

    [Fact]
    public async Task Prompt_RecordsPlainLanguageActivityWhileTheTurnRuns()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "do the thing", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();

        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\",\"title\":\"Read foo.cs\",\"kind\":\"read\",\"status\":\"running\"}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"messageId\":\"m1\",\"content\":{\"type\":\"text\",\"text\":\"I will read the file\"}}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"agent_thought_chunk\",\"messageId\":\"m1\",\"content\":{\"type\":\"text\",\"text\":\"Considering the options\"}}");

        // The read loop handles frames asynchronously — wait for them to land in the feed.
        await WaitUntilAsync(() => (_turnTracker.Current?.Activity?.Count ?? 0) >= 4);

        var activity = _turnTracker.Current!.Activity!;
        Assert.Contains(activity, a => a.Kind == TurnActivityKind.Tool && a.Label == "Read foo.cs");
        Assert.Contains(activity, a => a.Kind == TurnActivityKind.Text && a.Label.Contains("I will read the file"));
        Assert.Contains(activity, a => a.Kind == TurnActivityKind.Thought && a.Label.Contains("Considering the options"));
        Assert.NotNull(_turnTracker.Current.LastEventAt);

        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":10,\"outputTokens\":1,\"totalTokens\":11}}");
        await pending;
    }

    [Fact]
    public async Task Prompt_KeepsTheFinalToolCallStatus_FromToolCallUpdates()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "do the thing", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();

        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\",\"title\":\"Run the tests\",\"kind\":\"bash\",\"status\":\"running\"}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"t1\",\"title\":\"Run the tests\",\"kind\":\"bash\",\"status\":\"completed\",\"rawOutput\":\"42 passed\"}");
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":10,\"outputTokens\":1,\"totalTokens\":11}}");
        await pending;

        await using var db = CreateFactory().CreateDbContext();
        var part = await db.Parts.SingleAsync(p => p.ToolCallId == "t1");
        // The update used to be broadcast only, so the stored call kept the *initial* (empty)
        // output forever. Upserting by id is what makes this the final result.
        Assert.Equal("42 passed", part.OutputJson!.Value.GetString());
    }

    [Fact]
    public async Task NewSession_AppliesTheRequestedModelToTheAgent()
    {
        // Regression: the requested model used to be written to our row and never sent to the
        // agent, so opencode kept running its own model while the UI claimed otherwise.
        await using var coordinator = CreateCoordinator();

        var pending = coordinator.NewSessionAsync(@"C:\work\proj", "anthropic/claude-x", null, CancellationToken.None);
        var (initMethod, initId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", initMethod);
        _harness.Reply(initId, InitializeResultJson);
        var (newSessionMethod, newSessionId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        _harness.Reply(newSessionId, NewSessionResultJson);

        // The frame that never used to be sent at all.
        var (setModelMethod, setModelId, setModelParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", setModelMethod);
        Assert.Contains("anthropic/claude-x", setModelParams);
        _harness.Reply(setModelId, "{}");

        var session = await pending;
        Assert.Equal("anthropic/claude-x", session.ModelId);
        Assert.Equal("anthropic/claude-x", session.RequestedModelId);
    }

    [Fact]
    public async Task NewSession_WhenTheAgentRefusesTheModel_RecordsWhatIsActuallyInEffect()
    {
        await using var coordinator = CreateCoordinator();

        var pending = coordinator.NewSessionAsync(@"C:\work\proj", "nope/not-a-model", null, CancellationToken.None);
        var (_, initId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(initId, InitializeResultJson);
        var (_, newSessionId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(newSessionId, NewSessionResultJson);
        var (_, setModelId, _) = await _harness.ReadRequestAsync();
        _harness.ReplyError(setModelId, -32602, "Unknown model");

        var session = await pending;

        // Ask for one thing, record what is really running — and keep working.
        Assert.Equal("nope/not-a-model", session.RequestedModelId);
        Assert.Equal("opencode/big-pickle", session.ModelId);
    }

    [Fact]
    public async Task RecreatedSession_ReappliesTheModelTheSessionWasUsing()
    {
        // A new ACP session starts on the agent's own model; without re-applying, a broker
        // restart would silently change the model mid-release.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await SetStoredModelAsync(session.SessionId, "anthropic/claude-x");

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, promptId, _) = await _harness.ReadRequestAsync();
        _harness.ReplyError(promptId, -32602, "Invalid params: session not found: ses_abc");

        var (recreateMethod, recreateId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", recreateMethod);
        _harness.Reply(recreateId, NewSessionResultJson);

        var (setModelMethod, setModelId, setModelParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", setModelMethod);
        Assert.Contains("anthropic/claude-x", setModelParams);
        _harness.Reply(setModelId, "{}");

        var (setModeMethod, setModeId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_mode", setModeMethod);
        _harness.Reply(setModeId, "{}");

        var (retryMethod, retryId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", retryMethod);
        _harness.Reply(retryId, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":1,\"outputTokens\":1,\"totalTokens\":2}}");
        await pending;
    }

    [Fact]
    public async Task ConfigOptionUpdate_UpdatesTheStoredModel()
    {
        // opencode reports its own model changes; dropping them let our record drift from reality.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"config_option_update\",\"configOptions\":[{\"id\":\"model\",\"currentValue\":\"other/model\"}]}");
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":1,\"outputTokens\":1,\"totalTokens\":2}}");
        await pending;

        await WaitUntilAsync(() =>
            coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None).GetAwaiter().GetResult()?.ModelId == "other/model");

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("other/model", detail!.ModelId);
    }

    // Recreating a lost ACP session re-applies the model and mode that session was using before
    // the caller retries, so those two frames must be answered first.
    private async Task ReplyToReappliedModelAndModeAsync()
    {
        var (modelMethod, modelId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", modelMethod);
        _harness.Reply(modelId, "{}");

        var (modeMethod, modeId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_mode", modeMethod);
        _harness.Reply(modeId, "{}");
    }

    private async Task SetStoredModelAsync(Guid sessionId, string modelId)
    {
        await using var db = CreateFactory().CreateDbContext();
        var session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
        session.ModelId = modelId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Prompt_WhenTheModelIsRefused_SwitchesToTheNextModelAndRetries()
    {
        // The promise of keeping a list: a rate-limited model is skipped and the same prompt is
        // tried on the next one, instead of the turn dying.
        var watcher = new CapturingWatcher();
        var candidates = new ModelCandidateService(CreateFactory());
        await using var coordinator = CreateCoordinator(
            providerFailureWatcher: watcher, modelCandidates: candidates);
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        await _harness.ReadRequestAsync(); // the first attempt

        watcher.Fire(new ProviderFailure(ProviderFailureKind.RateLimited, "The AI service is rate-limiting this model.", TimeSpan.FromMinutes(3)));

        var (switchMethod, switchId, switchParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", switchMethod);
        Assert.Contains("opencode-go/kimi-k3", switchParams); // the seeded second entry
        _harness.Reply(switchId, "{}");

        var (retryMethod, retryId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", retryMethod);
        _harness.Reply(retryId, "{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":1,\"outputTokens\":1,\"totalTokens\":2}}");

        var result = await pending;
        Assert.Equal("end_turn", result.StopReason);

        // The refused model is cooled down, so the next turn goes straight to the working one.
        var after = await candidates.ListAsync(@"C:\work\proj", CancellationToken.None);
        Assert.NotNull(after[0].CooldownUntil);
        Assert.Equal("RateLimited", after[0].LastFailureKind);
    }

    [Fact]
    public async Task Prompt_WhenEveryModelIsRefused_StillFailsWithTheProvidersReason()
    {
        var watcher = new CapturingWatcher();
        var candidates = new ModelCandidateService(CreateFactory());

        // Every candidate already unusable: nothing left to switch to, so the turn must fail with
        // the provider's reason rather than silently retrying forever.
        foreach (var candidate in await candidates.ListSeededAsync(@"C:\work\proj", CancellationToken.None))
            await candidates.SetEnabledAsync(candidate.Id, false, CancellationToken.None);

        await using var coordinator = CreateCoordinator(
            providerFailureWatcher: watcher, modelCandidates: candidates);
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        await _harness.ReadRequestAsync();

        watcher.Fire(new ProviderFailure(ProviderFailureKind.ModelInvalid, "This model isn't available.", TimeSpan.FromMinutes(30)));

        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => pending);
        Assert.Contains("isn't available", error.PlainReason);
    }

    private sealed class CapturingWatcher : IProviderFailureWatcher
    {
        private Action<ProviderFailure>? _current;

        public IDisposable Watch(string acpSessionId, Action<ProviderFailure> onFailure)
        {
            _current = onFailure;
            return new Noop();
        }

        public void Fire(ProviderFailure failure) => _current?.Invoke(failure);

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition was not met in time");
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

        // Recreating re-applies the model and mode the session was using…
        await ReplyToReappliedModelAndModeAsync();

        // …and only then is the originally requested switch retried against the new session.
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

        // Recreating re-applies what the session was actually using, so the fresh ACP session
        // doesn't silently revert to the agent's own model/mode.
        var (reapplyModelMethod, reapplyModelId, reapplyModelParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_model", reapplyModelMethod);
        Assert.Contains("opencode/big-pickle", reapplyModelParams);
        _harness.Reply(reapplyModelId, "{}");

        var (reapplyModeMethod, reapplyModeId, reapplyModeParams) = await _harness.ReadRequestAsync();
        Assert.Equal("session/set_mode", reapplyModeMethod);
        // SetModeAsync records the requested mode before calling the agent, so the recreation
        // re-applies that same (pending) value — what the session will be left using.
        Assert.Contains("plan", reapplyModeParams);
        _harness.Reply(reapplyModeId, "{}");

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

    // ─── context instrumentation ─────────────────────────────────────────────

    [Fact]
    public async Task PromptWithSessionRecovery_RecordsContextOccupancyObservedMidTurn()
    {
        // The agent reports how much of its context is in use mid-turn but usually omits usage from
        // the final result, so before this the recorded ContextTokens was always null and there was
        // no way to tell a cheap turn from one working against a full context window. The number
        // recorded is the occupancy ("used"), not the window size, which is fixed per model and so
        // would say nothing about how much the turn was actually paying to prefill.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":130000,\"size\":200000}");
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");

        var result = await pending;

        Assert.NotNull(result.Measurement);
        Assert.Equal(130_000, result.Measurement!.ContextTokens);
    }

    [Fact]
    public async Task PromptWithSessionRecovery_RecordsTheLatestContextSizeSoAShrinkingContextIsVisible()
    {
        // A context size that DROPS mid-turn is the only signal available that pruning or
        // compaction actually happened: opencode has no compaction event on ACP today, so the
        // before/after comparison has to be read off these numbers.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":130000,\"size\":200000}");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":21000,\"size\":200000}");
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");

        var result = await pending;

        Assert.Equal(21_000, result.Measurement!.ContextTokens);
    }

    [Fact]
    public async Task PromptWithSessionRecovery_LeavesContextSizeNullWhenTheAgentNeverReportsIt()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");

        var result = await pending;

        Assert.Null(result.Measurement!.ContextTokens);
    }

    // ─── context measurement reliability ─────────────────────────────────────
    //
    // opencode emits usage_update in the same millisecond as the session/prompt response, so which
    // of the two the broker observes first is a race. Reading the context the instant the reply
    // arrived meant the reply usually won: ContextTokens was populated on 1 turn out of 262, which
    // is why "is the context actually being cleaned?" could not be answered from the data at all.
    // Context also belongs to the session, not to one turn, so a reading that lands just after a
    // turn closes must still count.

    [Fact]
    public async Task PromptWithSessionRecovery_ReadsTheSessionContextSoALaterTurnWithoutItsOwnReadingIsNotReportedAsEmpty()
    {
        // The agent only reports context at the end of a turn, and a turn that fails, is cancelled
        // or is a slash command never reports one at all. Reading the number off the turn therefore
        // reports "no context" for those turns when in fact nothing was learned — reading it off
        // the session reports what is still true, and the staleness is visible as a missing sample
        // rather than as a fabricated zero.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var first = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, firstId, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":130000,\"size\":200000}");
        _harness.Reply(firstId, "{\"stopReason\":\"end_turn\"}");
        await first;

        var second = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "again", CancellationToken.None);
        var (_, secondId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(secondId, "{\"stopReason\":\"end_turn\"}");

        var result = await second;

        Assert.Equal(130_000, result.Measurement!.ContextTokens);
    }

    [Fact]
    public async Task PromptWithSessionRecovery_DoesNotHangWhenTheAgentNeverReportsContext()
    {
        // Guards the bounded wait the turn does to catch a usage_update racing the reply. Waiting
        // for a notification that will never arrive would stall every errored, cancelled and
        // slash-handled turn, so the wait has to expire and has to expire quickly.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");

        var started = DateTime.UtcNow;
        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pending, completed);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "the wait for a late usage_update must stay short");
    }

    [Fact]
    public async Task PromptWithSessionRecovery_StillRecordsAUsageUpdateThatArrivesWellAfterTheTurnClosed()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");
        await pending;

        // Long after the turn is over and _turn has been cleared. Events are dispatched to the
        // active turn, and a closed turn has none, so this reading used to be discarded outright.
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":88000,\"size\":200000}");

        var samples = await WaitForContextSamplesAsync(session.SessionId, 1);
        Assert.Equal(88_000, samples[0].UsedTokens);
    }

    [Fact]
    public async Task UsageUpdates_ArePersistedAsATimeSeriesRatherThanOnlyTheLastReading()
    {
        // Keeping one number per turn throws away the evidence. The shape of the curve is what
        // distinguishes pruning working (a sawtooth) from context simply growing, so every reading
        // is kept with the window size needed to interpret it.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc", "{\"sessionUpdate\":\"usage_update\",\"used\":1000,\"size\":200000}");
        _harness.EmitSessionUpdate("ses_abc", "{\"sessionUpdate\":\"usage_update\",\"used\":12000,\"size\":200000}");
        _harness.EmitSessionUpdate("ses_abc", "{\"sessionUpdate\":\"usage_update\",\"used\":30000,\"size\":200000}");
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");
        await pending;

        var samples = await WaitForContextSamplesAsync(session.SessionId, 3);
        Assert.Equal([1000, 12_000, 30_000], samples.Select(s => s.UsedTokens).ToArray());
        Assert.All(samples, s => Assert.Equal(200_000, s.ContextSize));
        Assert.All(samples, s => Assert.Equal(ContextSampleSource.AgentUsage, s.Source));
    }

    [Fact]
    public async Task UsageUpdate_KeepsTheCumulativeCostOpenCodeReports()
    {
        // The cost is parsed off the wire and then dropped on the floor, which made it impossible
        // to tell an expensive turn from a cheap one without cross-referencing the billing site.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var pending = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":42000,\"size\":200000,"
            + "\"cost\":{\"amount\":1.75,\"currency\":\"USD\"}}");
        _harness.Reply(idRaw, "{\"stopReason\":\"end_turn\"}");
        await pending;

        var samples = await WaitForContextSamplesAsync(session.SessionId, 1);
        Assert.Equal(1.75m, samples[0].CostAmount);
        Assert.Equal("USD", samples[0].CostCurrency);
    }

    [Fact]
    public async Task FeatureBoundaryReset_RecordsAZeroedSampleSoTheDropIsVisibleAfterwards()
    {
        // The feature-boundary reset is the one moment the context is known to have been emptied
        // outright. Writing it down is what lets a later query prove the reset happened instead of
        // inferring it from a drop that pruning could equally have caused.
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await RecordTurnFeatureAsync(session.SessionId, "feature-alpha");
        _harness.EmitSessionUpdate("ses_abc",
            "{\"sessionUpdate\":\"usage_update\",\"used\":120000,\"size\":200000}");

        var pending = coordinator.ResetContextOnFeatureChangeAsync(
            session.SessionId, "feature-beta", CancellationToken.None);
        var (newSessionMethod, newSessionId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        _harness.Reply(newSessionId, FreshSessionResultJson);
        await ReplyToAsync("session/set_model");
        await ReplyToAsync("session/set_mode");
        await pending;

        var samples = await WaitForContextSamplesAsync(session.SessionId, 2);
        var reset = Assert.Single(samples, s => s.Source == ContextSampleSource.BoundaryReset);
        Assert.Equal(0, reset.UsedTokens);
        Assert.Equal("feature-beta", reset.FeatureKey);
    }

    private async Task<List<ContextUsageSample>> WaitForContextSamplesAsync(
        Guid sessionId, int expected, int timeoutMs = 5000)
    {
        // Samples are written off the event thread, so an eventually-consistent wait is the honest
        // assertion here rather than an artificial sleep.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            await using var db = CreateFactory().CreateDbContext();
            var samples = db.ContextUsageSamples
                .Where(s => s.SessionId == sessionId)
                .OrderBy(s => s.Id)
                .ToList();
            if (samples.Count >= expected)
                return samples;
            if (DateTime.UtcNow >= deadline)
                return samples;
            await Task.Delay(25);
        }
    }

    // ─── feature context boundary ────────────────────────────────────────────

    [Fact]
    public async Task ResetContextOnFeatureChange_SameFeature_KeepsTheSessionAndSpendsNoAgentCall()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await RecordTurnFeatureAsync(session.SessionId, "feature-alpha");

        var reset = await coordinator
            .ResetContextOnFeatureChangeAsync(session.SessionId, "feature-alpha", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(reset);
        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("ses_abc", detail!.AcpSessionId);
    }

    [Fact]
    public async Task ResetContextOnFeatureChange_FeatureMatchIsCaseInsensitive()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await RecordTurnFeatureAsync(session.SessionId, "Feature-Alpha");

        var reset = await coordinator
            .ResetContextOnFeatureChangeAsync(session.SessionId, "feature-alpha", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(reset);
    }

    [Fact]
    public async Task ResetContextOnFeatureChange_NeverUsedSession_HasNothingToDiscard()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);

        var reset = await coordinator
            .ResetContextOnFeatureChangeAsync(session.SessionId, "feature-beta", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(reset);
        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("ses_abc", detail!.AcpSessionId);
    }

    [Fact]
    public async Task ResetContextOnFeatureChange_DifferentFeature_OpensAFreshAgentSession()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await RecordTurnFeatureAsync(session.SessionId, "feature-alpha");

        var pending = coordinator.ResetContextOnFeatureChangeAsync(
            session.SessionId, "feature-beta", CancellationToken.None);

        var (newSessionMethod, newSessionId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        _harness.Reply(newSessionId, FreshSessionResultJson);
        await ReplyToAsync("session/set_model");
        await ReplyToAsync("session/set_mode");

        var reset = await pending;
        Assert.NotNull(reset);
        Assert.Equal("feature-alpha", reset!.PreviousFeatureKey);
        Assert.Equal("ses_fresh", reset.AcpSessionId);

        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Equal("ses_fresh", detail!.AcpSessionId);
    }

    [Fact]
    public async Task ResetContextOnFeatureChange_PreservesTheStoredTranscript()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await RecordTurnFeatureAsync(session.SessionId, "feature-alpha");

        var prompt = coordinator.PromptWithSessionRecoveryAsync(session.SessionId, "hello", CancellationToken.None);
        var (_, promptId, _) = await _harness.ReadRequestAsync();
        _harness.Reply(promptId, "{\"stopReason\":\"end_turn\"}");
        await prompt;

        var pending = coordinator.ResetContextOnFeatureChangeAsync(
            session.SessionId, "feature-beta", CancellationToken.None);
        var (newSessionMethod, newSessionId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        _harness.Reply(newSessionId, FreshSessionResultJson);
        await ReplyToAsync("session/set_model");
        await ReplyToAsync("session/set_mode");
        await pending;

        // Only the model's context is reset — DevTeam's own history must survive it, which is the
        // whole reason a reset is safe to do automatically.
        var detail = await coordinator.GetSessionDetailAsync(session.SessionId, CancellationToken.None);
        Assert.Contains(detail!.Messages, m => m.Role == "user" && m.BodyText == "hello");
    }

    [Fact]
    public async Task PromptForFeature_ResetsTheContextWhenTheFeatureChanges()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await RecordTurnFeatureAsync(session.SessionId, "feature-alpha");

        var pending = FeatureContextBoundary.PromptForFeatureAsync(
            coordinator, session.SessionId, "feature-beta", "kick off", CancellationToken.None);

        var (newSessionMethod, newSessionId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/new", newSessionMethod);
        _harness.Reply(newSessionId, FreshSessionResultJson);
        await ReplyToAsync("session/set_model");
        await ReplyToAsync("session/set_mode");

        var (promptMethod, promptId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", promptMethod);
        _harness.Reply(promptId, "{\"stopReason\":\"end_turn\"}");

        var result = await pending;
        Assert.Equal("end_turn", result.StopReason);
    }

    [Fact]
    public async Task PromptForFeature_SameFeature_PromptsWithoutOpeningANewSession()
    {
        await using var coordinator = CreateCoordinator();
        var session = await CreateSessionAsync(coordinator);
        await RecordTurnFeatureAsync(session.SessionId, "feature-alpha");

        var pending = FeatureContextBoundary.PromptForFeatureAsync(
            coordinator, session.SessionId, "feature-alpha", "keep going", CancellationToken.None);

        var (promptMethod, promptId, _) = await _harness.ReadRequestAsync();
        Assert.Equal("session/prompt", promptMethod);
        _harness.Reply(promptId, "{\"stopReason\":\"end_turn\"}");

        var result = await pending;
        Assert.Equal("end_turn", result.StopReason);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private async Task ReplyToAsync(string expectedMethod)
    {
        var (method, idRaw, _) = await _harness.ReadRequestAsync();
        Assert.Equal(expectedMethod, method);
        _harness.Reply(idRaw, "{}");
    }

    private async Task RecordTurnFeatureAsync(Guid sessionId, string featureKey)
    {
        await using var db = CreateFactory().CreateDbContext();
        db.TurnMetrics.Add(new TurnMetric { SessionId = sessionId, FeatureKey = featureKey });
        await db.SaveChangesAsync();
    }

    private readonly ActiveTurnTracker _turnTracker = new();

    private BrokerCoordinator CreateCoordinator(
        TimeSpan? stallAfter = null,
        IProviderFailureWatcher? providerFailureWatcher = null,
        IModelCandidateService? modelCandidates = null)
    {
        var spoke = new OpencodeAcpSpoke(_harness.Process);
        return new BrokerCoordinator(
            spoke, CreateFactory(), _broadcaster, NullLogger<BrokerCoordinator>.Instance, _turnTracker,
            stallAfter, providerFailureWatcher, modelCandidates);
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

    private const string FreshSessionResultJson =
        "{\"sessionId\":\"ses_fresh\",\"configOptions\":["
        + "{\"id\":\"model\",\"name\":\"Model\",\"category\":\"model\",\"type\":\"select\","
        + "\"currentValue\":\"opencode/big-pickle\","
        + "\"options\":[{\"value\":\"opencode/big-pickle\",\"name\":\"OpenCode Big Pickle\"}]},"
        + "{\"id\":\"mode\",\"name\":\"Mode\",\"category\":\"provider\",\"type\":\"select\","
        + "\"currentValue\":\"build\","
        + "\"options\":[{\"value\":\"build\",\"name\":\"Build\"},{\"value\":\"plan\",\"name\":\"Plan\"}]}]}";

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