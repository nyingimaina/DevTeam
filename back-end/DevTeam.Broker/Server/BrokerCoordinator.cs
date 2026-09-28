using System.Text;
using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Models;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Server;

/// <summary>
/// Owns the agent spoke and the session store. Turns typed ACP events into
/// StreamEvents, persists them, and broadcasts them to hub subscribers.
/// Prompts are serialized per broker (one opencode process, one active turn).
/// </summary>
public sealed class BrokerCoordinator : IAsyncDisposable, IWorkflowCoordinator
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

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentSpoke _spoke;
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly IEventBroadcaster _broadcaster;
    private readonly ILogger<BrokerCoordinator> _logger;
    private readonly ActiveTurnTracker _turnTracker;
    private readonly IProviderFailureWatcher? _providerFailureWatcher;
    private readonly IModelCandidateService? _modelCandidates;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private TurnCollector? _turn;
    private AgentInfo? _agentInfo;

    public BrokerCoordinator(
        IAgentSpoke spoke,
        IDbContextFactory<DevTeamDbContext> dbFactory,
        IEventBroadcaster broadcaster,
        ILogger<BrokerCoordinator> logger,
        ActiveTurnTracker turnTracker,
        TimeSpan? stallAfter = null,
        IProviderFailureWatcher? providerFailureWatcher = null,
        IModelCandidateService? modelCandidates = null)
    {
        _spoke = spoke;
        _dbFactory = dbFactory;
        _broadcaster = broadcaster;
        _logger = logger;
        _turnTracker = turnTracker;
        _providerFailureWatcher = providerFailureWatcher;
        _modelCandidates = modelCandidates;
        _spoke.EventReceived += OnEventReceived;

        _stallAfter = stallAfter ?? TimeSpan.FromMinutes(5);
        // Check often enough to notice within a fraction of the budget, but never so often it
        // spins (a test passes a tiny budget and still gets a sane interval).
        _stallCheckInterval = _stallAfter < TimeSpan.FromSeconds(45)
            ? TimeSpan.FromMilliseconds(Math.Max(25, _stallAfter.TotalMilliseconds / 3))
            : TimeSpan.FromSeconds(15);

        try
        {
            using var db = _dbFactory.CreateDbContext();
            db.Database.EnsureCreated();
            DevTeamDbContextSchemaSync.EnsureAllTablesCreated(db);
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
        string workspacePath, string? modelId, IReadOnlyList<string>? allowedWritePrefixes, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var acpSession = await _spoke.NewSessionAsync(workspacePath, cancellationToken);

        var ambientModelId = acpSession.ConfigOptions.FirstOrDefault(o => o.Id == "model")?.CurrentValue;
        var effectiveModeId = acpSession.ConfigOptions.FirstOrDefault(o => o.Id == "mode")?.CurrentValue;

        // session/new carries only a cwd, so the model has to be applied explicitly. Recording
        // the requested id without telling the agent is what made the UI claim a model that was
        // never in use.
        var appliedModelId = await ApplyModelAsync(acpSession.SessionId, modelId, ambientModelId, cancellationToken);

        var entity = new DevTeamSession
        {
            WorkspacePath = workspacePath,
            AcpSessionId = acpSession.SessionId,
            ModelId = appliedModelId,
            RequestedModelId = modelId,
            ModeId = effectiveModeId,
            AllowedWritePrefixesJson = allowedWritePrefixes is null ? null : JsonSerializer.Serialize(allowedWritePrefixes),
        };

        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Sessions.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }

        var session = ToSummary(entity, acpSession.ConfigOptions);
        _logger.LogInformation(
            "Created session {SessionId} for {Workspace} (model requested={Requested}, applied={Applied})",
            entity.Id, workspacePath, modelId ?? "(none)", appliedModelId ?? "(unknown)");
        return session;
    }

    /// <summary>
    /// Tells the agent which model to use, and reports what is actually in effect. A refused or
    /// unknown model leaves the agent's own model in place rather than failing the session — the
    /// caller records both so the difference is visible instead of silent.
    /// </summary>
    private async Task<string?> ApplyModelAsync(
        string acpSessionId, string? requestedModelId, string? ambientModelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedModelId))
            return ambientModelId;

        try
        {
            await _spoke.SetModelAsync(acpSessionId, requestedModelId, cancellationToken);
            return requestedModelId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not apply model {Model} to ACP session {AcpSessionId}; the agent's own model ({Ambient}) stays in effect.",
                requestedModelId, acpSessionId, ambientModelId ?? "(unknown)");
            return ambientModelId;
        }
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
                .ToArray(),
            entity.RequestedModelId);
    }

    /// <summary>
    /// Recovers when the agent process no longer recognizes the session's stored ACP id
    /// (e.g. the broker restarted and respawned opencode since this session was created):
    /// it transparently opens a fresh ACP session for the same workspace, persists the
    /// new id, and retries once.
    /// </summary>
    public async Task<PromptResponse> PromptWithSessionRecoveryAsync(
        Guid sessionId, string text, CancellationToken cancellationToken, bool isPriming = false, string? displayText = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        // Observational only: lets the UI say how many steps are ahead of this one rather than
        // an open-ended "waiting". Disposed the moment the slot is acquired.
        var queued = _turnTracker.BeginQueued();
        var queueStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _turnLock.WaitAsync(cancellationToken);
        }
        finally
        {
            queued.Dispose();
            queueStopwatch.Stop();
        }

        if (queueStopwatch.ElapsedMilliseconds > 250)
        {
            _logger.LogInformation(
                "Turn for session {SessionId} waited {WaitedMs}ms for the single agent slot (another turn was running).",
                sessionId, queueStopwatch.ElapsedMilliseconds);
        }
        // Declared before the try so the catch/finally clauses can see them (a local declared
        // inside a try block is not in scope in its catch/finally).
        var stalled = false;
        // Set when the agent's own log shows the model provider refused the request. opencode
        // never reports this over ACP, so without reading its log the turn would just hang.
        ProviderFailure? providerFailure = null;
        List<CancellationTokenSource> replacedTurnCtses = [];
        CancellationTokenSource? turnCts = null;
        using var watchdogStop = new CancellationTokenSource();
        var promptStopwatch = new System.Diagnostics.Stopwatch();
        try        {
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
                    // Store the human's words, not the instructions DevTeam appends for the model.
                    BodyText = displayText ?? text,
                    IsPriming = isPriming,
                });
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            _turn = new TurnCollector(sessionId);
            var turnStartedAt = DateTimeOffset.UtcNow;

            turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var turnScope = _turnTracker.Begin(sessionId, acpSessionId, displayText ?? text, turnCts, isPriming);

            // Watchdog: a healthy turn emits a steady trickle of events. If the stream goes
            // completely silent for StallAfter, treat it as stalled and end the turn now —
            // otherwise it would wait out the 30-minute request budget, which the user
            // experiences as the app hanging with no explanation.
            void StartStallWatch()
            {
                _ = WatchForStallAsync(sessionId, watchdogStop.Token, () =>
                {
                    stalled = true;
                    turnCts.Cancel();
                });
            }
            StartStallWatch();

            // The agent's own log is the only place a provider refusal shows up; watching it lets
            // the turn end with the real reason in seconds instead of waiting out the stall
            // watchdog and then blaming the agent.
            //
            // Each attempt gets its own token: abandoning a refused prompt must NOT cancel the
            // turn, or the retry on the next model would be cancelled before it was even sent.
            async Task<AgentPromptResult> PromptOnceAsync(CancellationTokenSource attemptCts)
            {
                ProviderFailure? failure = null;
                using var failureWatch = _providerFailureWatcher?.Watch(acpSessionId, f =>
                {
                    failure = f;
                    attemptCts.Cancel();
                });

                try
                {
                    return await _spoke.PromptAsync(
                        acpSessionId,
                        [new AgentPromptPart(PromptPartTypeText, text)],
                        attemptCts.Token);
                }
                catch (OperationCanceledException) when (failure is { IsFailure: true })
                {
                    // Contain it here: the cancellation came from the provider refusing, not from a
                    // person, so the loop below can just try a different model.
                    providerFailure = failure;
                    throw new ProviderUnavailableException(
                        failure.PlainReason, null, failure.Kind == ProviderFailureKind.RateLimited);
                }
            }

            // A stuck/slow opencode turn looks identical to a broken request from the UI's
            // perspective (it just "hangs") — logging elapsed time here is the difference
            // between "the agent is legitimately slow" and "something is actually wedged".
            promptStopwatch.Start();
            _logger.LogInformation(
                "Prompting session {SessionId} (acp {AcpSessionId}, isPriming={IsPriming}, {TextLength} chars)",
                sessionId, acpSessionId, isPriming, text.Length);

            AgentPromptResult result;
            var triedModels = new List<string>();
            var recreations = 0;
            var stallSwitches = 0;
            while (true)
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(turnCts.Token);
                try
                {
                    result = await PromptOnceAsync(attemptCts);
                    break;
                }
                catch (RpcException ex) when (IsAcpSessionNotFound(ex))
                {
                    if (recreations++ >= 1)
                        throw;

                    _logger.LogWarning(
                        "ACP session {AcpSessionId} for {SessionId} was not recognized by the agent " +
                        "(likely a broker restart); opening a new agent session and retrying.",
                        acpSessionId, sessionId);

                    acpSessionId = await RecreateAcpSessionAsync(sessionId, workspacePath, turnCts.Token);
                    _turn = new TurnCollector(sessionId);
                }
                catch (AcpDisconnectedException ex)
                {
                    // The opencode process itself died mid-request (not just a stale session id,
                    // which the case above already covers) — same recovery: fresh session, retry
                    // once. If this also fails, the outer catch below classifies and rethrows.
                    if (recreations++ >= 1)
                        throw;

                    _logger.LogWarning(ex,
                        "ACP process for session {SessionId} disconnected; opening a new agent session and retrying.",
                        sessionId);

                    acpSessionId = await RecreateAcpSessionAsync(sessionId, workspacePath, turnCts.Token);
                    _turn = new TurnCollector(sessionId);
                }
                catch (OperationCanceledException) when (stalled)
                {
                    var stallEx = new AcpStalledException(_stallAfter);
                    _logger.LogWarning(
                        "Turn for session {SessionId} produced no output for {Minutes} minutes; treating it as stalled.",
                        sessionId, _stallAfter.TotalMinutes);
                    var silenceRefusal = new ProviderUnavailableException(
                        stallEx.Message, modelId: null, isRateLimit: false);
                    // None — not turnCts.Token: the watchdog already cancelled that token, and the
                    // switch decision (model list + persistence) must not inherit that cancellation.
                    if (stallSwitches >= 1
                       || !await TrySwitchToNextModelAsync(sessionId, workspacePath, acpSessionId, triedModels, silenceRefusal, CancellationToken.None))
                    {
                        throw;
                    }

                    // Fresh turn token (the old one was cancelled by the watchdog) and a fresh
                    // watchdog, so the retry is judged on its own silence, not the old one's.
                    stallSwitches++;
                    stalled = false;
                    var replaced = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    replacedTurnCtses.Add(turnCts);
                    turnCts = replaced;
                    StartStallWatch();
                    _turnTracker.Record(TurnActivityKind.Status, "The model went silent — retrying on the next candidate");
                }
                catch (ProviderUnavailableException ex)
                {
                    // A refused model shouldn't end the turn if another one is available — that is
                    // the whole point of keeping a list. Only escalate when nothing is left to try.
                    if (!await TrySwitchToNextModelAsync(sessionId, workspacePath, acpSessionId, triedModels, ex, turnCts.Token))
                        throw;
                }
            }

            promptStopwatch.Stop();
            // Time-to-first-event is the number that explains "it looked hung": a turn that never
            // produced anything shows -1 here, which is exactly what a stall looks like.
            _logger.LogInformation(
                "Session {SessionId} turn finished: stopReason={StopReason} elapsedMs={ElapsedMs} timeToFirstEventMs={FirstEventMs} textEvents={TextEvents} thoughtEvents={ThoughtEvents} toolEvents={ToolEvents}",
                sessionId, result.StopReason, promptStopwatch.ElapsedMilliseconds,
                _turn?.FirstEventAt is { } first ? (long)(first - turnStartedAt).TotalMilliseconds : -1,
                _turn?.TextEventCount ?? 0, _turn?.ThoughtEventCount ?? 0, _turn?.ToolEventCount ?? 0);

            _turn!.SetUsage(result.Usage);
            _turnTracker.Record(TurnActivityKind.Status, "The agent finished this step");
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
                result.Usage?.TotalTokens ?? 0,
                new TurnMeasurement(
                    promptStopwatch.ElapsedMilliseconds,
                    _turn?.FirstEventAt is { } firstEvent
                        ? (long)(firstEvent - turnStartedAt).TotalMilliseconds
                        : null,
                    _turn?.TextEventCount ?? 0,
                    _turn?.ThoughtEventCount ?? 0,
                    _turn?.ToolEventCount ?? 0,
                    Outcome: "Ok",
                    CachedReadTokens: result.Usage?.CachedReadTokens));
        }
        catch (OperationCanceledException) when (providerFailure is { IsFailure: true })
        {
            // The model provider refused the request. Report *its* reason, not "the agent stopped
            // responding" — that difference is the whole point of reading the agent's log.
            promptStopwatch.Stop();
            _logger.LogWarning(
                "Turn for session {SessionId} refused by the model provider ({Kind}) after {ElapsedMs}ms: {Reason}",
                sessionId, providerFailure.Kind, promptStopwatch.ElapsedMilliseconds, providerFailure.PlainReason);
            throw new ProviderUnavailableException(
                providerFailure.PlainReason,
                modelId: null,
                isRateLimit: providerFailure.Kind == ProviderFailureKind.RateLimited);
        }
        catch (OperationCanceledException) when (stalled)
        {
            // The watchdog said the stream went silent — a failure the user must see and retry,
            // not a cancellation. This is the difference between "hung forever" and "recovered
            // in minutes".
            promptStopwatch.Stop();
            _logger.LogWarning(
                "Turn for session {SessionId} produced no output for {Minutes} minutes; treating it as stalled.",
                sessionId, _stallAfter.TotalMinutes);
            throw new AcpStalledException(_stallAfter);
        }
        catch (OperationCanceledException)
        {
            // A user-cancelled turn (or a client that went away) is expected, not a failure:
            // don't log it as an error and don't tell the UI the prompt failed.
            _logger.LogInformation("Turn for session {SessionId} was cancelled.", sessionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prompt failed for session {SessionId}.", sessionId);
            await FireAsync(sessionId, EventPromptError, new { ex.Message }, CancellationToken.None);
            throw;
        }
        finally
        {
            // Stop the stall watchdog the moment the turn ends, whatever the outcome.
            if (!watchdogStop.IsCancellationRequested)
                watchdogStop.Cancel();
            foreach (var replaced in replacedTurnCtses)
                replaced.Dispose();
            turnCts?.Dispose();
            _turn = null;
            _turnLock.Release();
        }
    }

    // A healthy turn emits progress constantly (tool calls, thoughts, text). This is how long a
    // total silence may last before the turn is treated as stalled — deliberately generous, but
    // far shorter than the 30-minute request budget a hung turn would otherwise burn.
    private readonly TimeSpan _stallAfter;
    private readonly TimeSpan _stallCheckInterval;

    private async Task WatchForStallAsync(Guid sessionId, CancellationToken stop, Action onStall)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_stallCheckInterval, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var info = _turnTracker.Current;
            if (info is null || info.SessionId != sessionId)
                return;

            var lastEventAt = info.LastEventAt ?? info.StartedAt;
            var silentFor = DateTimeOffset.UtcNow - lastEventAt;
            _logger.LogDebug(
                "Stall check for session {SessionId}: silent for {SilentMs}ms (budget {BudgetMs}ms)",
                sessionId, (long)silentFor.TotalMilliseconds, (long)_stallAfter.TotalMilliseconds);

            if (silentFor >= _stallAfter)
            {
                onStall();
                return;
            }
        }
    }

    /// <summary>Gets whatever agent turn is currently running (or stuck), if any.</summary>
    public ActiveTurnInfo? GetCurrentTurn() => _turnTracker.Current;

    /// <summary>
    /// Cancels whatever agent turn is currently running (or stuck), freeing the turn
    /// lock for the next queued caller. Best-effort tells the agent to stop too.
    /// </summary>
    public async Task<bool> CancelCurrentTurnAsync(CancellationToken cancellationToken)
    {
        if (!_turnTracker.TryCancelCurrent(out var info) || info is null)
            return false;

        try
        {
            await _spoke.CancelAsync(info.AcpSessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify the agent of cancellation for session {AcpSessionId}.", info.AcpSessionId);
        }

        return true;
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

        // A fresh ACP session starts on the agent's own model/mode. Without re-applying them, a
        // broker restart silently changes which model a release is using mid-flight.
        var ambient = acpSession.ConfigOptions.FirstOrDefault(o => o.Id == "model")?.CurrentValue;
        session.ModelId = await ApplyModelAsync(acpSession.SessionId, session.ModelId, ambient, cancellationToken);

        if (!string.IsNullOrWhiteSpace(session.ModeId))
        {
            try
            {
                await _spoke.SetModeAsync(acpSession.SessionId, session.ModeId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Could not re-apply mode {Mode} to recreated ACP session {AcpSessionId}.",
                    session.ModeId, acpSession.SessionId);
            }
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return acpSession.SessionId;
    }

    public async Task<string> SetModelAsync(
        Guid sessionId, string modelId, CancellationToken cancellationToken)
    {
        string acpSessionId;
        string workspacePath;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
            acpSessionId = session.AcpSessionId;
            workspacePath = session.WorkspacePath;
            session.ModelId = modelId;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await RememberModelForWorkspaceAsync(db, workspacePath, modelId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await _spoke.SetModelAsync(acpSessionId, modelId, cancellationToken);
        }
        catch (RpcException ex) when (IsAcpSessionNotFound(ex))
        {
            _logger.LogWarning(
                "ACP session {AcpSessionId} for {SessionId} was not recognized by the agent " +
                "(likely a broker restart); opening a new agent session and retrying.",
                acpSessionId, sessionId);
            var recreated = await RecreateAcpSessionAsync(sessionId, workspacePath, cancellationToken);
            await _spoke.SetModelAsync(recreated, modelId, cancellationToken);
        }

        _logger.LogInformation("Session {SessionId} switched to model {Model}", sessionId, modelId);
        return modelId;
    }

    // Choosing a model anywhere — the stage header picker or the provider-refused recovery pane,
    // which both land here — also makes it this project's remembered model, so the next stage
    // (and the next release in this workspace) starts on it instead of the built-in default.
    private static async Task RememberModelForWorkspaceAsync(
        DevTeamDbContext db, string workspacePath, string modelId, CancellationToken cancellationToken)
    {
        var settings = await db.WorkspaceModelSettings
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath, cancellationToken);

        if (settings is null)
        {
            db.WorkspaceModelSettings.Add(new WorkspaceModelSettings
            {
                WorkspacePath = workspacePath,
                ModelId = modelId,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return;
        }

        settings.ModelId = modelId;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task<string> SetModeAsync(
        Guid sessionId, string modeId, CancellationToken cancellationToken)
    {
        string acpSessionId;
        string workspacePath;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
            acpSessionId = session.AcpSessionId;
            workspacePath = session.WorkspacePath;
            session.ModeId = modeId;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await _spoke.SetModeAsync(acpSessionId, modeId, cancellationToken);
        }
        catch (RpcException ex) when (IsAcpSessionNotFound(ex))
        {
            _logger.LogWarning(
                "ACP session {AcpSessionId} for {SessionId} was not recognized by the agent " +
                "(likely a broker restart); opening a new agent session and retrying.",
                acpSessionId, sessionId);
            var recreated = await RecreateAcpSessionAsync(sessionId, workspacePath, cancellationToken);
            await _spoke.SetModeAsync(recreated, modeId, cancellationToken);
        }

        _logger.LogInformation("Session {SessionId} switched to mode {Mode}", sessionId, modeId);
        return modeId;
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
                turn.AppendText(text.Text);
                turn.NoteEvent(AgentEventKind.Text);
                turn.MessageId ??= text.MessageId;
                RecordSnippet(turn, TurnActivityKind.Text, isThought: false);
                _ = FireAsync(turn.SessionId, EventTextDelta, new { text.MessageId, text.Text }, CancellationToken.None);
                break;
            case AgentThoughtDelta thought:
                turn.AppendThought(thought.Text);
                turn.NoteEvent(AgentEventKind.Thought);
                RecordSnippet(turn, TurnActivityKind.Thought, isThought: true);
                _ = FireAsync(turn.SessionId, EventThoughtDelta, new { thought.MessageId, thought.Text }, CancellationToken.None);
                break;
            case AgentToolCallEvent call:
                turn.NoteEvent(AgentEventKind.Tool);
                turn.UpsertToolCall(call.Call);
                RecordToolCall(turn, call.Call);
                WriteToolCall(turn.SessionId, EventToolCall, call.Call);
                break;
            case AgentToolCallUpdatedEvent call:
                turn.NoteEvent(AgentEventKind.Tool);
                // Upsert (not just broadcast): the update carries the final status/output, which
                // used to be discarded — leaving the stored tool call permanently half-filled.
                turn.UpsertToolCall(call.Call);
                RecordToolCall(turn, call.Call);
                WriteToolCall(turn.SessionId, EventToolCallUpdated, call.Call);
                break;
            case AgentUsageUpdatedEvent usage:
                // Not worth a feed line, but it IS proof the stream is alive — keep the heartbeat
                // honest so a busy turn is never mistaken for a stalled one.
                _turnTracker.Touch();
                _ = FireAsync(turn.SessionId, EventUsageUpdated,
                    new { Usage = new UsageDto(usage.Usage.UsedTokens, usage.Usage.ContextSize,
                        usage.Usage.UsedTokens, null) },
                    CancellationToken.None);
                break;
            case AgentConfigOptionsUpdatedEvent options:
                _turnTracker.Touch();
                // The agent tells us when its own model/mode changes. Dropping that (as we used
                // to) let our record drift from reality, so the UI showed a model not in use.
                _ = PersistConfigOptionsAsync(turn.SessionId, options.Options);
                _ = FireAsync(turn.SessionId, EventConfigOptionsUpdated,
                    new { Options = options.Options.Select(ToConfigOptionDto).ToArray() },
                    CancellationToken.None);
                break;
            default:
                _logger.LogDebug("Ignoring agent event {Type}", @event.GetType().Name);
                break;
        }
    }

    // Records a coalesced snippet of streamed text/thoughts — see TurnCollector.SnippetFor.
    private void RecordSnippet(TurnCollector turn, string kind, bool isThought)
    {
        if (turn.SnippetFor(isThought) is { Length: > 0 } snippet)
            _turnTracker.Record(kind, snippet);
    }

    private void RecordToolCall(TurnCollector turn, AgentToolCallInfo call)
    {
        // Only when something actually changed: an update that merely re-states the current
        // status would otherwise put the same line in the feed several times.
        if (!turn.ShouldRecordTool(call))
            return;

        var label = !string.IsNullOrWhiteSpace(call.Title) ? call.Title!
            : !string.IsNullOrWhiteSpace(call.Kind) ? call.Kind!
            : "The agent used a tool";
        _turnTracker.Record(TurnActivityKind.Tool, label, status: call.Status);
    }

    // Records the agent's *live* model/mode when it reports a change, so our stored value stays
    // the truth the UI can rely on.
    private async Task PersistConfigOptionsAsync(Guid sessionId, IReadOnlyList<AgentConfigOption> options)
    {
        var modelId = options.FirstOrDefault(o => o.Id == "model")?.CurrentValue;
        var modeId = options.FirstOrDefault(o => o.Id == "mode")?.CurrentValue;
        if (string.IsNullOrWhiteSpace(modelId) && string.IsNullOrWhiteSpace(modeId))
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);
            if (session is null)
                return;

            var changed = false;
            if (!string.IsNullOrWhiteSpace(modelId) && session.ModelId != modelId)
            {
                session.ModelId = modelId;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(modeId) && session.ModeId != modeId)
            {
                session.ModeId = modeId;
                changed = true;
            }
            if (!changed)
                return;

            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist a config option update for session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Cools down the model that was refused and points the session at the next candidate, so the
    /// caller can simply retry. Returns false when there is nothing left worth trying, which is
    /// the caller's signal to escalate with the real reason.
    /// </summary>
    private async Task<bool> TrySwitchToNextModelAsync(
        Guid sessionId, string workspacePath, string acpSessionId, List<string> triedModels,
        ProviderUnavailableException refusal, CancellationToken ct)
    {
        if (_modelCandidates is null)
        {
            return false;
        }

        var currentModelId = await GetStoredModelIdAsync(sessionId, ct);
        if (currentModelId is not null && !triedModels.Contains(currentModelId))
            triedModels.Add(currentModelId);

        var candidates = await _modelCandidates.ListSeededAsync(workspacePath, ct);

        var failed = candidates.FirstOrDefault(c => c.ModelId == currentModelId);
        if (failed is not null)
        {
            // Rate limits are transient, so a short pause is enough; anything else gets the
            // cautious cooldown so we don't keep hammering a broken endpoint.
            await _modelCandidates.RecordFailureAsync(failed.Id, new ProviderFailure(
                refusal.IsRateLimit ? ProviderFailureKind.RateLimited : ProviderFailureKind.Unknown,
                refusal.PlainReason,
                refusal.IsRateLimit ? ProviderFailureClassifier.RateLimitCooldown : ProviderFailureClassifier.UnavailableCooldown),
                ct);
        }

        var next = await _modelCandidates.ResolveActiveAsync(workspacePath, ct);
        if (next is null || triedModels.Contains(next.ModelId) || triedModels.Count >= candidates.Count)
        {
            // Everthing else is already cooked by the previous refusal flows within: hint what
            // a caller is left with here to save the next reader a full model-list re-derive.
            return false;
        }
        await _spoke.SetModelAsync(acpSessionId, next.ModelId, ct);
        await SetStoredModelIdAsync(sessionId, next.ModelId, ct);
        triedModels.Add(next.ModelId);

        _turnTracker.Record(TurnActivityKind.Status, $"Switched to {next.ModelId} — the previous model was refused");
        _logger.LogWarning(
            "Model {Previous} was refused ({Reason}); switched session {SessionId} to {Next} and retrying.",
            currentModelId ?? "(unknown)", refusal.PlainReason, sessionId, next.ModelId);
        return true;
    }

    private async Task<string?> GetStoredModelIdAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.ModelId).FirstOrDefaultAsync(ct);
    }

    private async Task SetStoredModelIdAsync(Guid sessionId, string modelId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
            return;

        session.ModelId = modelId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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
                payload is null ? null : JsonSerializer.SerializeToElement(payload, PayloadJsonOptions));
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
            modes,
            s.RequestedModelId);
    }

    private static ConfigOptionDto ToConfigOptionDto(AgentConfigOption o) => new(
        o.Id,
        o.CurrentValue ?? string.Empty,
        o.Options.Select(v => new ModelOption(v.Value, v.Name, v.Description)).ToArray());

    internal static MessageDto ToMessageDto(Message m) => new(
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
            .ToArray(),
        m.IsPriming);

    // The kinds of inbound agent event worth counting per turn (see TurnCollector.NoteEvent).
    private enum AgentEventKind
    {
        Text,
        Thought,
        Tool,
    }

    private sealed class TurnCollector(Guid sessionId)
    {
        // A turn emits thousands of tiny text/thought chunks; recording one entry per chunk would
        // flood the feed (and every poll response), so a snippet is emitted at most this often.
        private static readonly TimeSpan SnippetMinInterval = TimeSpan.FromSeconds(2.5);

        // How much of the latest text/thought is shown in a feed entry.
        private const int SnippetChars = 140;

        public Guid SessionId { get; } = sessionId;
        public StringBuilder Text { get; } = new();
        public List<AgentToolCallInfo> ToolCalls { get; } = [];
        public string? MessageId { get; set; }
        public AgentUsageInfo? Usage { get; private set; }

        private string _thoughtTail = string.Empty;
        private DateTimeOffset _lastTextAt = DateTimeOffset.MinValue;
        private DateTimeOffset _lastThoughtAt = DateTimeOffset.MinValue;
        private readonly Dictionary<string, string?> _lastToolStatus = new();

        /// <summary>When the agent first produced anything for this turn, and how much of each kind.</summary>
        public DateTimeOffset? FirstEventAt { get; private set; }

        public int TextEventCount { get; private set; }
        public int ThoughtEventCount { get; private set; }
        public int ToolEventCount { get; private set; }

        public void NoteEvent(AgentEventKind kind)
        {
            FirstEventAt ??= DateTimeOffset.UtcNow;
            switch (kind)
            {
                case AgentEventKind.Text: TextEventCount++; break;
                case AgentEventKind.Thought: ThoughtEventCount++; break;
                case AgentEventKind.Tool: ToolEventCount++; break;
            }
        }

        public void SetUsage(AgentUsageInfo? usage) => Usage = usage;

        public void AppendText(string chunk) => Text.Append(chunk);

        public void AppendThought(string chunk)
        {
            // Only the tail is ever shown, so cap the buffer rather than let a long turn grow it.
            _thoughtTail += chunk;
            if (_thoughtTail.Length > SnippetChars * 4)
                _thoughtTail = _thoughtTail[^SnippetChars..];
        }

        /// <summary>A snippet worth showing, or null when this chunk should be coalesced away.</summary>
        public string? SnippetFor(bool isThought)
        {
            var now = DateTimeOffset.UtcNow;
            var lastAt = isThought ? _lastThoughtAt : _lastTextAt;
            if (now - lastAt < SnippetMinInterval)
                return null;

            if (isThought) _lastThoughtAt = now;
            else _lastTextAt = now;

            var source = isThought ? _thoughtTail : Text.ToString();
            return Tail(source);
        }

        /// <summary>True when this tool call's status is new — so the feed shows one line per change.</summary>
        public bool ShouldRecordTool(AgentToolCallInfo call)
        {
            if (_lastToolStatus.TryGetValue(call.ToolCallId, out var previous) && previous == call.Status)
                return false;

            _lastToolStatus[call.ToolCallId] = call.Status;
            return true;
        }

        /// <summary>
        /// Upsert by id so a later <c>tool_call_update</c> completes the call's status/output
        /// instead of being dropped — that data used to be lost, leaving a half-filled history row.
        /// </summary>
        public void UpsertToolCall(AgentToolCallInfo call)
        {
            var index = ToolCalls.FindIndex(c => c.ToolCallId == call.ToolCallId);
            if (index >= 0) ToolCalls[index] = call;
            else ToolCalls.Add(call);
        }

        private static string Tail(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length <= SnippetChars ? trimmed : "…" + trimmed[^SnippetChars..].Trim();
        }
    }
}