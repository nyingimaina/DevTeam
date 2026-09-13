using System.Text;
using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Spoke;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Server;

/// <summary>
/// Owns the agent spoke and the session store. Turns typed ACP events into
/// StreamEvents, persists them, and broadcasts them to hub subscribers.
/// Prompts are serialized per broker (one opencode process, one active turn).
/// </summary>
public sealed class BrokerCoordinator : IAsyncDisposable
{
    public const string EventTextDelta = "textDelta";
    public const string EventThoughtDelta = "thoughtDelta";
    public const string EventToolCall = "toolCall";
    public const string EventToolCallUpdated = "toolCallUpdated";
    public const string EventUsageUpdated = "usageUpdated";
    public const string EventConfigOptionsUpdated = "configOptionsUpdated";
    public const string EventPromptError = "error";
    public const string EventTurnEnd = "turnEnd";

    public const string PromptPartTypeText = "text";

    private readonly IAgentSpoke _spoke;
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly IEventBroadcaster _broadcaster;
    private readonly ILogger<BrokerCoordinator> _logger;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private TurnCollector? _turn;
    private AgentInfo? _agentInfo;

    public BrokerCoordinator(
        IAgentSpoke spoke,
        IDbContextFactory<DevTeamDbContext> dbFactory,
        IEventBroadcaster broadcaster,
        ILogger<BrokerCoordinator> logger)
    {
        _spoke = spoke;
        _dbFactory = dbFactory;
        _broadcaster = broadcaster;
        _logger = logger;
        _spoke.EventReceived += OnEventReceived;

        try
        {
            using var db = _dbFactory.CreateDbContext();
            db.Database.EnsureCreated();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed; session persistence is unavailable.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _spoke.EventReceived -= OnEventReceived;
        _turnLock.Dispose();
        _initLock.Dispose();
        _spoke.Dispose();
    }

    private async Task<AgentInfo> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_agentInfo is { } cached)
            return cached;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_agentInfo is { } cachedAfterLock)
                return cachedAfterLock;

            _agentInfo = await _spoke.InitializeAsync(cancellationToken);
            return _agentInfo;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<AgentViewModel> GetAgentInfoAsync(CancellationToken cancellationToken)
    {
        var info = await EnsureInitializedAsync(cancellationToken);
        return new AgentViewModel(
            ProtocolVersion: "1",
            AgentName: info.Name,
            AgentVendor: "anomalyco",
            AgentVersion: info.Version,
            Models: []);
    }

    public async Task<SessionSummary> NewSessionAsync(
        string workspacePath, string? modelId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var acpSession = await _spoke.NewSessionAsync(workspacePath, cancellationToken);

        var effectiveModelId = modelId
            ?? acpSession.ConfigOptions.FirstOrDefault(o => o.Id == "model")?.CurrentValue;
        var effectiveModeId = acpSession.ConfigOptions.FirstOrDefault(o => o.Id == "mode")?.CurrentValue;

        var entity = new DevTeamSession
        {
            WorkspacePath = workspacePath,
            AcpSessionId = acpSession.SessionId,
            ModelId = effectiveModelId,
            ModeId = effectiveModeId,
        };

        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Sessions.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }

        var session = ToSummary(entity, acpSession.ConfigOptions);
        _logger.LogInformation("Created session {SessionId} for {Workspace}", entity.Id, workspacePath);
        return session;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sessions = await db.Sessions.ToListAsync(cancellationToken);
        return sessions
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => ToSummary(s, []))
            .ToArray();
    }

    public async Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Sessions
            .Include(s => s.Messages)
            .ThenInclude(m => m.Parts)
            .SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (entity is null)
            return null;

        return new SessionDetail(
            entity.Id,
            entity.AcpSessionId,
            entity.WorkspacePath,
            entity.Title,
            entity.ModelId,
            entity.ModeId,
            entity.CreatedAt,
            entity.UpdatedAt,
            [],
            [],
            entity.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(ToMessageDto)
                .ToArray());
    }

    public async Task<PromptResponse> PromptAsync(
        Guid sessionId, string text, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _turnLock.WaitAsync(cancellationToken);
        try
        {
            string acpSessionId;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
                acpSessionId = session.AcpSessionId;
                db.Messages.Add(new Message
                {
                    SessionId = session.Id,
                    Role = "user",
                    BodyText = text,
                });
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            _turn = new TurnCollector(sessionId);

            var result = await _spoke.PromptAsync(
                acpSessionId,
                [new AgentPromptPart(PromptPartTypeText, text)],
                cancellationToken);

            _turn!.SetUsage(result.Usage);
            await PersistAssistantTurnAsync(sessionId, _turn, cancellationToken);
            await FireAsync(sessionId, EventTurnEnd, new
            {
                result.StopReason,
                Usage = result.Usage is null ? null : new UsageDto(
                    result.Usage.InputTokens, result.Usage.OutputTokens,
                    result.Usage.TotalTokens, result.Usage.CachedReadTokens),
            }, cancellationToken);

            return new PromptResponse(
                sessionId,
                result.StopReason,
                result.Usage?.InputTokens ?? 0,
                result.Usage?.OutputTokens ?? 0,
                result.Usage?.TotalTokens ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prompt failed for session {SessionId}.", sessionId);
            await FireAsync(sessionId, EventPromptError, new { ex.Message }, CancellationToken.None);
            throw;
        }
        finally
        {
            _turn = null;
            _turnLock.Release();
        }
    }

    /// <summary>
    /// Same as <see cref="PromptAsync"/>, but recovers when the agent process no longer
    /// recognizes the session's stored ACP id (e.g. the broker restarted and respawned
    /// opencode since this session was created): it transparently opens a fresh ACP
    /// session for the same workspace, persists the new id, and retries once.
    /// </summary>
    public async Task<PromptResponse> PromptWithSessionRecoveryAsync(
        Guid sessionId, string text, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _turnLock.WaitAsync(cancellationToken);
        try
        {
            string acpSessionId;
            string workspacePath;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
                acpSessionId = session.AcpSessionId;
                workspacePath = session.WorkspacePath;
                db.Messages.Add(new Message
                {
                    SessionId = session.Id,
                    Role = "user",
                    BodyText = text,
                });
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            _turn = new TurnCollector(sessionId);

            AgentPromptResult result;
            try
            {
                result = await _spoke.PromptAsync(
                    acpSessionId,
                    [new AgentPromptPart(PromptPartTypeText, text)],
                    cancellationToken);
            }
            catch (RpcException ex) when (IsAcpSessionNotFound(ex))
            {
                _logger.LogWarning(
                    "ACP session {AcpSessionId} for {SessionId} was not recognized by the agent " +
                    "(likely a broker restart); opening a new agent session and retrying.",
                    acpSessionId, sessionId);

                acpSessionId = await RecreateAcpSessionAsync(sessionId, workspacePath, cancellationToken);
                _turn = new TurnCollector(sessionId);
                result = await _spoke.PromptAsync(
                    acpSessionId,
                    [new AgentPromptPart(PromptPartTypeText, text)],
                    cancellationToken);
            }

            _turn!.SetUsage(result.Usage);
            await PersistAssistantTurnAsync(sessionId, _turn, cancellationToken);
            await FireAsync(sessionId, EventTurnEnd, new
            {
                result.StopReason,
                Usage = result.Usage is null ? null : new UsageDto(
                    result.Usage.InputTokens, result.Usage.OutputTokens,
                    result.Usage.TotalTokens, result.Usage.CachedReadTokens),
            }, cancellationToken);

            return new PromptResponse(
                sessionId,
                result.StopReason,
                result.Usage?.InputTokens ?? 0,
                result.Usage?.OutputTokens ?? 0,
                result.Usage?.TotalTokens ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prompt failed for session {SessionId}.", sessionId);
            await FireAsync(sessionId, EventPromptError, new { ex.Message }, CancellationToken.None);
            throw;
        }
        finally
        {
            _turn = null;
            _turnLock.Release();
        }
    }

    private static bool IsAcpSessionNotFound(RpcException ex)
        => ex.Message.Contains("session not found", StringComparison.OrdinalIgnoreCase);

    private async Task<string> RecreateAcpSessionAsync(
        Guid sessionId, string workspacePath, CancellationToken cancellationToken)
    {
        var acpSession = await _spoke.NewSessionAsync(workspacePath, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.SingleAsync(s => s.Id == sessionId, cancellationToken);
        session.AcpSessionId = acpSession.SessionId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return acpSession.SessionId;
    }

    public async Task<string> SetModelAsync(
        Guid sessionId, string modelId, CancellationToken cancellationToken)
    {
string acpSessionId;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
                acpSessionId = session.AcpSessionId;
                session.ModelId = modelId;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

        await _spoke.SetModelAsync(acpSessionId, modelId, cancellationToken);
        _logger.LogInformation("Session {SessionId} switched to model {Model}", sessionId, modelId);
        return modelId;
    }

    public async Task<string> SetModeAsync(
        Guid sessionId, string modeId, CancellationToken cancellationToken)
    {
        string acpSessionId;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
            acpSessionId = session.AcpSessionId;
            session.ModeId = modeId;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        await _spoke.SetModeAsync(acpSessionId, modeId, cancellationToken);
        _logger.LogInformation("Session {SessionId} switched to mode {Mode}", sessionId, modeId);
        return modeId;
    }

    public async Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
            return false;

        db.Sessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted session {SessionId}", sessionId);
        return true;
    }

    // ─── event handling ───────────────────────────────────────────────────────

    private void OnEventReceived(object? sender, AgentEvent @event)
    {
        var turn = _turn;
        if (turn is null)
        {
            _logger.LogDebug("Ignoring agent event outside the active turn: {Type}", @event.GetType().Name);
            return;
        }

        switch (@event)
        {
            case AgentTextDelta text:
                turn.Text.Append(text.Text);
                turn.MessageId ??= text.MessageId;
                _ = FireAsync(turn.SessionId, EventTextDelta, new { text.MessageId, text.Text }, CancellationToken.None);
                break;
            case AgentThoughtDelta thought:
                _ = FireAsync(turn.SessionId, EventThoughtDelta, new { thought.MessageId, thought.Text }, CancellationToken.None);
                break;
            case AgentToolCallEvent call:
                turn.ToolCalls.Add(call.Call);
                WriteToolCall(turn.SessionId, EventToolCall, call.Call);
                break;
            case AgentToolCallUpdatedEvent call:
                WriteToolCall(turn.SessionId, EventToolCallUpdated, call.Call);
                break;
            case AgentUsageUpdatedEvent usage:
                _ = FireAsync(turn.SessionId, EventUsageUpdated,
                    new { Usage = new UsageDto(usage.Usage.UsedTokens, usage.Usage.ContextSize,
                        usage.Usage.UsedTokens, null) },
                    CancellationToken.None);
                break;
            case AgentConfigOptionsUpdatedEvent options:
                _ = FireAsync(turn.SessionId, EventConfigOptionsUpdated,
                    new { Options = options.Options.Select(ToConfigOptionDto).ToArray() },
                    CancellationToken.None);
                break;
            default:
                _logger.LogDebug("Ignoring agent event {Type}", @event.GetType().Name);
                break;
        }
    }

    private void WriteToolCall(Guid sessionId, string type, AgentToolCallInfo call)
    {
        _ = FireAsync(sessionId, type, new
        {
            call.ToolCallId,
            call.Title,
            call.Kind,
            call.Status,
            RawInput = call.RawInput?.ValueKind == JsonValueKind.Undefined ? null : call.RawInput,
            RawOutput = call.RawOutput?.ValueKind == JsonValueKind.Undefined ? null : call.RawOutput,
        }, CancellationToken.None);
    }

    private async Task PersistAssistantTurnAsync(
        Guid sessionId, TurnCollector turn, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var assistant = new Message
        {
            SessionId = sessionId,
            Role = "assistant",
            AcpMessageId = turn.MessageId,
            BodyText = turn.Text.Length == 0 ? null : turn.Text.ToString(),
        };
        db.Messages.Add(assistant);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var call in turn.ToolCalls)
        {
            db.Parts.Add(new Part
            {
                MessageId = assistant.Id,
                Kind = "tool_call",
                ToolCallId = call.ToolCallId,
                ToolName = call.Kind,
                InputJson = call.RawInput,
                OutputJson = call.RawOutput,
            });
        }

        db.AuditEvents.Add(new AuditEvent
        {
            SessionId = sessionId,
            Kind = "prompt",
            PayloadJson = JsonSerializer.SerializeToElement(new
            {
                usage = turn.Usage is null ? null : new
                {
                    turn.Usage.InputTokens,
                    turn.Usage.OutputTokens,
                    turn.Usage.TotalTokens,
                    turn.Usage.CachedReadTokens,
                },
            }),
        });

        var session = await db.Sessions.SingleAsync(s => s.Id == sessionId, cancellationToken);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FireAsync(Guid sessionId, string type, object? payload, CancellationToken cancellationToken)
    {
        try
        {
            var streamEvent = new StreamEvent(
                sessionId.ToString(),
                type,
                payload is null ? null : JsonSerializer.SerializeToElement(payload));
            await _broadcaster.BroadcastAsync(streamEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Broadcast failed for {Type} on {SessionId}.", type, sessionId);
        }
    }

    private static SessionSummary ToSummary(DevTeamSession s, IReadOnlyList<AgentConfigOption> configOptions)
    {
        var models = configOptions
            .FirstOrDefault(o => o.Id == "model")
            ?.Options
            .Select(o => new ModelOption(o.Value, o.Name, o.Description))
            .ToArray() ?? [];

        var modes = configOptions
            .FirstOrDefault(o => o.Id == "mode")
            ?.Options
            .Select(o => new ModelOption(o.Value, o.Name, o.Description))
            .ToArray() ?? [];

        return new SessionSummary(
            s.Id,
            s.AcpSessionId,
            s.WorkspacePath,
            s.Title,
            s.ModelId,
            s.ModeId,
            s.CreatedAt,
            s.UpdatedAt,
            models,
            modes);
    }

    private static ConfigOptionDto ToConfigOptionDto(AgentConfigOption o) => new(
        o.Id,
        o.CurrentValue ?? string.Empty,
        o.Options.Select(v => new ModelOption(v.Value, v.Name, v.Description)).ToArray());

    private static MessageDto ToMessageDto(Message m) => new(
        m.Id,
        m.Role,
        m.AcpMessageId,
        m.ProviderModelId,
        m.BodyText,
        m.CreatedAt,
        m.Parts
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PartDto(
                p.Id, p.Kind, p.Text, p.ToolName, p.ToolCallId, p.ErrorText,
                p.InputJson, p.OutputJson, p.CreatedAt))
            .ToArray());

    private sealed class TurnCollector(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;
        public StringBuilder Text { get; } = new();
        public List<AgentToolCallInfo> ToolCalls { get; } = [];
        public string? MessageId { get; set; }
        public AgentUsageInfo? Usage { get; private set; }

        public void SetUsage(AgentUsageInfo? usage) => Usage = usage;
    }
}