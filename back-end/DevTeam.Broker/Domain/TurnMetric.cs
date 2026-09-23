namespace DevTeam.Broker.Domain;

/// <summary>What caused a prompt — the field that makes a token total actionable.</summary>
public enum TurnKind
{
    Stage,
    Retry,
    Challenge,
    Delegation,
    Conflict,
    GatePrompt,
    Correction,
    Priming,
    Probe,
    Map,
}

public enum TurnOutcome
{
    Ok,
    Failed,
    Cancelled,
    ProviderRefused,
    Stalled,
}

/// <summary>
/// One agent turn, measured: what caused it, how long it took, what it cost, how big the prompt
/// was and what the prompt was made of. Written for every prompt so a stage's token total can be
/// broken down by cause (retries, the antagonist review, delegation, …) instead of being a
/// meaningless lump sum.
/// </summary>
public sealed class TurnMetric
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Nullable: a probe or a code-map write can happen outside a stage run.
    public Guid? StageRunId { get; set; }
    public ReleaseStageRun? StageRun { get; set; }

    public Guid? SessionId { get; set; }
    public string? WorkspacePath { get; set; }
    public string StageName { get; set; } = string.Empty;
    public string? FeatureKey { get; set; }
    public string? ModelId { get; set; }

    public TurnKind Kind { get; set; }
    public int Attempt { get; set; } = 1;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; set; }
    public long? TimeToFirstEventMs { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long? CachedReadTokens { get; set; }
    public long? ContextTokens { get; set; }
    public decimal? CostAmount { get; set; }
    public string? CostCurrency { get; set; }

    /// <summary>Total characters of the prompt, and its per-section split (JSON) — the token lever.</summary>
    public int PromptChars { get; set; }
    public string? PromptBreakdownJson { get; set; }

    public int TextEvents { get; set; }
    public int ThoughtEvents { get; set; }
    public int ToolEvents { get; set; }

    public string? StopReason { get; set; }
    public TurnOutcome Outcome { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A permanent daily rollup, so raw <see cref="TurnMetric"/> rows can be pruned after the retention
/// window without losing the long-term picture.
/// </summary>
public sealed class TurnMetricDaily
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UTC date as "yyyy-MM-dd" — sortable and unambiguous.</summary>
    public string Day { get; set; } = string.Empty;

    public string WorkspacePath { get; set; } = string.Empty;
    public string StageName { get; set; } = string.Empty;
    public TurnKind Kind { get; set; }
    public string ModelId { get; set; } = string.Empty;

    public int Turns { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long DurationMs { get; set; }
    public decimal? CostAmount { get; set; }
}
