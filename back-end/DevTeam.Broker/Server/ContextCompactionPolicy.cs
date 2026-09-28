namespace DevTeam.Broker.Server;

/// <summary>
/// Decides when the context is full enough to be worth compacting on the user's behalf.
/// </summary>
/// <remarks>
/// Deciding this is the risky part, not doing it. Compaction spends a model call, and repeating it
/// on a context that barely shrinks is a loop that gets more expensive without getting smaller. So
/// the decision is deliberately conservative: between turns only — never during one, which would
/// change the context out from under the running turn — and once the context has been compacted it
/// has to grow well past the line again before another is allowed. A skipped decision is recorded
/// with its reason, because "why did it run out of context when the bar never filled" is only
/// answerable if the near misses were written down.
/// </remarks>
public sealed record ContextCompactionPolicy
{
    /// <summary>Occupancy above which compaction is considered, as a fraction of the window.</summary>
    public const double DefaultThreshold = 0.75;

    /// <summary>
    /// How much of the window the context has to reclaim before another compaction is allowed.
    /// Without this, a compaction that frees little leaves the context still over the line and the
    /// next check fires again — which is exactly the loop this is here to prevent.
    /// </summary>
    private const double RefillHeadroom = 0.10;

    public double Threshold { get; init; } = DefaultThreshold;

    /// <summary>Context occupancy at the last compaction, or null if none has run.</summary>
    public long? CompactedAtUsedTokens { get; init; }

    public static ContextCompactionDecision Decide(
        ContextCompactionPolicy policy,
        ContextDto context,
        bool turnActive)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        if (turnActive)
            return ContextCompactionDecision.Skip("a turn is in progress");

        // Nothing known yet: there is no percentage to judge against, and compacting an unmeasured
        // context is spending a call on a guess.
        if (context.UpdatedAt is null)
            return ContextCompactionDecision.Skip("the agent has not reported a context reading yet");

        if (context.ContextSize is not { } size || size <= 0)
            return ContextCompactionDecision.Skip("the agent has not reported a context size yet");

        var occupancy = (double)context.UsedTokens / size;
        if (occupancy < policy.Threshold)
            return ContextCompactionDecision.Skip("below the threshold");

        if (policy.CompactedAtUsedTokens is { } compactedAt
            && context.UsedTokens < compactedAt + (long)(size * RefillHeadroom))
        {
            return ContextCompactionDecision.Skip(
                "already compacted, and the context has not refilled enough to justify doing it again");
        }

        return new ContextCompactionDecision(true, occupancy, null);
    }

    /// <summary>Records that a compaction ran, so the next one is measured from where it left off.</summary>
    public ContextCompactionPolicy AfterCompaction(long usedTokensAfter)
        => this with { CompactedAtUsedTokens = usedTokensAfter };
}

public sealed record ContextCompactionDecision(bool ShouldCompact, double? Occupancy, string? SkipReason)
{
    public static ContextCompactionDecision Skip(string reason) => new(false, null, reason);
}
