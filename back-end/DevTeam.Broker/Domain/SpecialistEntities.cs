namespace DevTeam.Broker.Domain;

/// <summary>
/// A named, reusable specialist an agent can delegate to from any stage in any workspace's
/// pipeline (see Part 3 of the dynamic-stages plan) — e.g. a "database-admin" who knows how
/// to prepare a migration safely. Global like Profile, not tied to one workspace or pipeline
/// position: any registered specialist is callable from any stage by name.
/// </summary>
public sealed class SpecialistRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Shown to a calling agent deciding whether this specialist can help, and to a
    /// human managing the roster.</summary>
    public string Description { get; set; } = string.Empty;

    public string PrimingPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Same meaning as WorkflowRole.WritesCode: false confines the specialist's session to the
    /// feature's docs directory; true additionally grants the feature's manifest-declared code
    /// paths. There is no broader custom-permission system in V1 — injecting real external
    /// credentials (a live DB connection, say) is a materially different, larger feature than
    /// delegation itself and is deliberately not built here.
    /// </summary>
    public bool WritesCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A record of one delegation round-trip — who was asked, what was asked, and what came back
/// — so it's visible in Stage Diagnostics instead of disappearing into the calling stage's
/// transcript.
/// </summary>
public sealed class SpecialistConsultation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StageRunId { get; set; }
    public ReleaseStageRun StageRun { get; set; } = null!;
    public string SpecialistName { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
