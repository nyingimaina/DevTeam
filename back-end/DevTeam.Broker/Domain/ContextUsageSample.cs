namespace DevTeam.Broker.Domain;

/// <summary>What a context reading represents, so a later query can tell a measurement from an event.</summary>
public enum ContextSampleSource
{
    /// <summary>A reading the agent reported about the context it was actually working in.</summary>
    AgentUsage,

    /// <summary>
    /// The context was emptied on purpose because the work moved to a different feature. Recorded
    /// as an explicit zero so the reset can be proven rather than inferred from a drop.
    /// </summary>
    BoundaryReset,

    /// <summary>The context was summarized to make room.</summary>
    Compaction,
}

/// <summary>
/// One point on the context-occupancy curve: how full the window was when the agent said so, how
/// big that window is, and what it cost.
///
/// Kept as a series rather than as a single number per turn on purpose. A compaction or a
/// feature-boundary reset shows up as a cliff in the curve, and the only way to tell that apart
/// from ordinary growth — or to tell pruning actually working from the context merely being small —
/// is to be able to read the readings that came before as well as the one that came after.
/// </summary>
public sealed class ContextUsageSample
{
    /// <summary>Monotonic so the samples come back in the order the agent reported them.</summary>
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>The agent session this was measured against, which changes on a boundary reset.</summary>
    public string? AcpSessionId { get; set; }

    public string? FeatureKey { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Tokens currently occupying the context window.</summary>
    public long UsedTokens { get; set; }

    /// <summary>Total size of the context window, as reported by the agent.</summary>
    public long? ContextSize { get; set; }

    public decimal? CostAmount { get; set; }
    public string? CostCurrency { get; set; }

    public ContextSampleSource Source { get; set; }
}
