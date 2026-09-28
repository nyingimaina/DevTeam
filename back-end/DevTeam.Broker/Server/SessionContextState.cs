using DevTeam.Broker.Domain;

namespace DevTeam.Broker.Server;

/// <summary>A context reading, as read by a caller that wants to know what is true right now.</summary>
public sealed record ContextUsageReading(long UsedTokens, long? ContextSize, decimal? CostAmount, string? CostCurrency);

/// <summary>
/// What is known about the context the agent is working in right now.
///
/// This lives on the session rather than on the turn, for two reasons that are both about not
/// lying to whoever reads the number:
///
/// <para>The agent only reports context at the end of a turn, and in the same millisecond as the
/// prompt response. Reading the context the instant the reply arrives loses that race most of the
/// time — the reply usually wins, and the number is never recorded. A turn therefore waits, for a
/// short bounded period, for the reading that belongs to it.</para>
///
/// <para>A turn that is never given a reading — one that failed, was cancelled, or was a slash
/// command — has not learned anything new about the context. Reporting that as an empty context
/// would invent a fact, so what is reported is the session's last known reading, and the fact that
/// it is stale is carried separately.</para>
/// </summary>
public sealed class SessionContextState
{
    private readonly object _gate = new();
    private TaskCompletionSource _turnReading = NewSignal();

    public long UsedTokens { get; private set; }
    public long? ContextSize { get; private set; }
    public decimal? CostAmount { get; private set; }
    public string? CostCurrency { get; private set; }
    public string? AcpSessionId { get; private set; }
    public string? FeatureKey { get; private set; }

    /// <summary>Change in occupancy from the previous reading, or null if there is nothing to compare to.</summary>
    public long? LastDeltaTokens { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>True while a turn is in flight, so a stale reading is not shown as if it were current.</summary>
    public bool TurnActive { get; private set; }

    /// <summary>How many times the context has been deliberately emptied.</summary>
    public int CompactionCount { get; private set; }

    /// <summary>Marks the start of a turn and arms the signal its context reading will arrive on.</summary>
    public void BeginTurn()
    {
        lock (_gate)
        {
            _turnReading = NewSignal();
            TurnActive = true;
        }
    }

    public void EndTurn()
    {
        lock (_gate)
        {
            TurnActive = false;
        }
    }

    /// <summary>Records what the agent reported about the context it was working in.</summary>
    public void NoteUsage(
        string? acpSessionId,
        long usedTokens,
        long? contextSize,
        decimal? costAmount,
        string? costCurrency,
        string? featureKey)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (AcpSessionId is not null)
                LastDeltaTokens = usedTokens - UsedTokens;

            UsedTokens = usedTokens;
            ContextSize = contextSize;
            CostAmount = costAmount;
            CostCurrency = costCurrency;
            AcpSessionId = acpSessionId ?? AcpSessionId;
            FeatureKey = featureKey ?? FeatureKey;
            UpdatedAt = DateTimeOffset.UtcNow;

            signal = _turnReading;
        }

        // Outside the lock: a continuation must never run while the lock is held.
        signal.TrySetResult();
    }

    /// <summary>
    /// Records that the context was emptied on purpose — a feature boundary or a compaction — so the
    /// drop can be proven from the data afterwards rather than inferred from a fall that ordinary
    /// pruning could equally have caused.
    /// </summary>
    public void Clear(string acpSessionId, ContextSampleSource source, string? featureKey)
    {
        lock (_gate)
        {
            AcpSessionId = acpSessionId;
            FeatureKey = featureKey ?? FeatureKey;
            UsedTokens = 0;
            ContextSize = null;
            CostAmount = null;
            CostCurrency = null;
            UpdatedAt = DateTimeOffset.UtcNow;
            LastDeltaTokens = null;
            if (source != ContextSampleSource.AgentUsage)
                CompactionCount++;
        }
    }

    /// <summary>
    /// Waits, for at most <paramref name="timeout"/>, for the reading belonging to the current turn.
    /// Returns null when none arrives, which is the normal case for a turn that never reached the
    /// model — never an error, just nothing learned.
    /// </summary>
    public async Task<ContextUsageReading?> WaitForUsageAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_gate)
            signal = _turnReading.Task;

        var completed = await Task.WhenAny(signal, Task.Delay(timeout, cancellationToken));
        if (completed != signal)
            return null;

        lock (_gate)
        {
            return new ContextUsageReading(UsedTokens, ContextSize, CostAmount, CostCurrency);
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
