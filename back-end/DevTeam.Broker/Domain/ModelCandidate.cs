namespace DevTeam.Broker.Domain;

/// <summary>
/// One entry in a workspace's ordered list of models to try. The app walks the list in
/// <see cref="Priority"/> order, skips anything disabled or cooling down, and moves to the next
/// entry when a model is refused — so a rate-limited provider degrades instead of stalling.
/// </summary>
public sealed class ModelCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>Agent model id, e.g. "opencode/big-pickle".</summary>
    public string ModelId { get; set; } = string.Empty;

    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>False for the seeded defaults (which carry curated cost/smartness stats).</summary>
    public bool UserAdded { get; set; }

    /// <summary>Set after a failure; the candidate is skipped until this passes.</summary>
    public DateTimeOffset? CooldownUntil { get; set; }

    public string? LastFailureKind { get; set; }
    public string? LastFailureReason { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
}
