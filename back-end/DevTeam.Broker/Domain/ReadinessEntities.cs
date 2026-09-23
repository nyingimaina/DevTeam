namespace DevTeam.Broker.Domain;

/// <summary>
/// One persisted readiness run (the result of the strict checks). Reports are kept per release
/// so the Checks library can show what was verified, when, and how it trended — the raw output
/// and metrics live on the child rows so the in-app report is rendered from data, never stored
/// as a pre-baked HTML file.
/// </summary>
public sealed class ReadinessReportRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkspacePath { get; set; } = string.Empty;
    public Guid? ReleaseId { get; set; }
    public Guid? FeatureId { get; set; }
    /// <summary>"feature" or "release" — see Gates.Readiness.ReadinessScope.</summary>
    public string Scope { get; set; } = "release";
    public bool Passed { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; set; }
    /// <summary>The release version this run verified, when it was a release-level run.</summary>
    public string? ReleaseVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReadinessCheckRow> Checks { get; set; } = [];
}

public sealed class ReadinessCheckRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReportId { get; set; }
    public ReadinessReportRow Report { get; set; } = null!;
    public string PhaseId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>"Passed", "Failed" or "Skipped".</summary>
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    /// <summary>Serialized Gates.Readiness.ReadinessMetrics — the numbers behind the charts.</summary>
    public string? MetricsJson { get; set; }
    public string? RawOutput { get; set; }
}

/// <summary>
/// Proof that a specific commit passed the strict checks. A merge into a protected branch is
/// only authorised when a passing attestation exists and the branch still points at the exact
/// commit it verified — so new work invalidates it and the checks must run again.
/// </summary>
public sealed class ReadinessAttestation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReleaseId { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public Guid ReportId { get; set; }
    public bool Passed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
