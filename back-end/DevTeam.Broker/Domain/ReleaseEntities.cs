using System.ComponentModel.DataAnnotations.Schema;

namespace DevTeam.Broker.Domain;

public enum ReleaseStatus
{
    Draft,
    InProgress,
    Blocked,
    Escalated,
    Ready,
    Released,
    Cancelled,
}

public enum ReleaseFeatureStatus
{
    Proposed,
    InProgress,
    Complete,
    OnHold,
}

public enum ReleaseStageStatus
{
    Pending,
    Active,
    GatesRunning,
    BlockedGate,
    BlockedSignoff,
    Escalated,
    Stale,
    Complete,
}

/// <summary>
/// Why a stage's most recent agent turn didn't complete normally — distinguishes cases
/// that warrant a blind retry (the agent process died, or it timed out) from one that
/// won't (the model provider itself rejected the request; retrying identically will
/// likely fail identically). See ReleaseStageRun.LastErrorKind.
/// </summary>
public enum StageErrorKind
{
    None,
    Disconnected,
    ProviderRejected,
    TimedOut,
}

public enum StagePhase
{
    GuidedQA,
    Producing,
    Gates,
    Challenge,
    Signoff,
}

public enum ReviewFindingKind
{
    Requirement,
    Code,
    Test,
    Security,
    Process,
}

public enum ReviewFindingSeverity
{
    Info,
    Minor,
    Major,
    Blocker,
}

public enum ReviewFindingStatus
{
    Open,
    Resolved,
    Escalated,
}

public sealed class DevTeamRelease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkspacePath { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string Version { get; set; } = "0.1.0";
    public ReleaseStatus Status { get; set; } = ReleaseStatus.Draft;
    /// <summary>Git branch this release ships from, e.g. "release/login-form". Set once, at creation.</summary>
    public string BranchName { get; set; } = string.Empty;
    /// <summary>
    /// The feature currently in flight on this release, if any. v1 simplification:
    /// only one feature can be worked at a time per release — the next one can't
    /// start (POST .../features) until this one completes and clears the field.
    /// </summary>
    public Guid? CurrentFeatureId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReleaseFeature> Features { get; set; } = [];

    /// <summary>The feature currently in flight, if loaded via Features. Never a real column.</summary>
    [NotMapped]
    private ReleaseFeature? CurrentFeature => Features.FirstOrDefault(f => f.Id == CurrentFeatureId);

    // Kept on the wire as release.stageRuns/signoffs/flowPosition (pre-GitFlow shape) by
    // proxying to whichever feature is currently in flight — the pipeline UI only ever
    // works one feature at a time, so this keeps the frontend/API contract unchanged
    // even though the data now actually lives on ReleaseFeature.
    [NotMapped]
    public List<ReleaseStageRun> StageRuns => CurrentFeature?.StageRuns ?? [];

    [NotMapped]
    public List<ReleaseSignoff> Signoffs => CurrentFeature?.Signoffs ?? [];

    [NotMapped]
    public ReleaseFlowPosition? FlowPosition => CurrentFeature?.FlowPosition;
}

public sealed class ReleaseFeature
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReleaseId { get; set; }
    public DevTeamRelease Release { get; set; } = null!;
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Git branch this feature is developed on, e.g. "feature/login-form". Set once, at creation.</summary>
    public string BranchName { get; set; } = string.Empty;
    public ReleaseFeatureStatus Status { get; set; } = ReleaseFeatureStatus.Proposed;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReleaseRequirement> Requirements { get; set; } = [];
    public FeatureSlice? Slice { get; set; }
    public List<ReleaseStageRun> StageRuns { get; set; } = [];
    public List<ReleaseSignoff> Signoffs { get; set; } = [];
    public ReleaseFlowPosition? FlowPosition { get; set; }
}

public sealed class ReleaseRequirement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FeatureId { get; set; }
    public ReleaseFeature Feature { get; set; } = null!;
    public string ReqId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AcceptanceCriteria { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FeatureSlice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FeatureId { get; set; }
    public ReleaseFeature Feature { get; set; } = null!;
    public string ArtifactDirectory { get; set; } = string.Empty;
    public string CodePathBack { get; set; } = string.Empty;
    public string CodePathFront { get; set; } = string.Empty;
    public string SharedFilesJson { get; set; } = "[]";
}

public sealed class ReleaseStageRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReleaseFeatureId { get; set; }
    public ReleaseFeature Feature { get; set; } = null!;
    public Guid? SessionId { get; set; }
    public DevTeamSession? Session { get; set; }
    public string? AcpSessionId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public ReleaseStageStatus Status { get; set; } = ReleaseStageStatus.Pending;
    public StagePhase Phase { get; set; } = StagePhase.GuidedQA;
    public int QuestionCount { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Summary { get; set; }

    /// <summary>
    /// True when the agent has said DONE and all of this stage's gates have passed — the
    /// engine's authoritative "ready to show the human a Proceed button" signal. Reset to
    /// false the moment a new human message is sent, so a stale readiness can't linger.
    /// </summary>
    public bool ReadyToProceed { get; set; }

    /// <summary>
    /// Classification of the most recent prompt failure for this stage run (session
    /// disconnected, provider rejected the request, or it timed out) — None when the
    /// last attempt succeeded or none has run yet. Cleared on the next successful prompt.
    /// </summary>
    public StageErrorKind LastErrorKind { get; set; } = StageErrorKind.None;
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }

    public List<ReleaseGateCheck> GateChecks { get; set; } = [];
    public List<ReviewFinding> Findings { get; set; } = [];
    public List<ReleaseGuidanceNote> GuidanceNotes { get; set; } = [];
}

public sealed class ReleaseGateCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StageRunId { get; set; }
    public ReleaseStageRun StageRun { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? EvidenceText { get; set; }
    public string? EvidencePath { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ReviewFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StageRunId { get; set; }
    public ReleaseStageRun StageRun { get; set; } = null!;
    public string Target { get; set; } = string.Empty;
    public ReviewFindingKind Kind { get; set; }
    public ReviewFindingSeverity Severity { get; set; } = ReviewFindingSeverity.Info;
    public string? RequirementRef { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? EvidencePath { get; set; }
    public ReviewFindingStatus Status { get; set; } = ReviewFindingStatus.Open;
    public Guid? ResolverStageRunId { get; set; }
    public ReleaseStageRun? ResolverStageRun { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class ReleaseSignoff
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReleaseFeatureId { get; set; }
    public ReleaseFeature Feature { get; set; } = null!;
    public string StageName { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public bool Approved { get; set; }
    public string? ApprovedBy { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
}

public sealed class ReleaseGuidanceNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StageRunId { get; set; }
    public ReleaseStageRun StageRun { get; set; } = null!;
    public string Text { get; set; } = string.Empty;
    public string? AddedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ReleaseUsageLedger
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StageRunId { get; set; }
    public ReleaseStageRun StageRun { get; set; } = null!;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ReleaseFlowPosition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReleaseFeatureId { get; set; }
    public ReleaseFeature Feature { get; set; } = null!;
    public int CurrentStageIndex { get; set; }
    public string CurrentStageName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}