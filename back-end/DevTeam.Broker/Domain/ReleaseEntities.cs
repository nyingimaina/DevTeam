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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReleaseFeature> Features { get; set; } = [];
    public List<ReleaseStageRun> StageRuns { get; set; } = [];
    public List<ReleaseSignoff> Signoffs { get; set; } = [];
    public ReleaseFlowPosition? FlowPosition { get; set; }
}

public sealed class ReleaseFeature
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReleaseId { get; set; }
    public DevTeamRelease Release { get; set; } = null!;
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ReleaseFeatureStatus Status { get; set; } = ReleaseFeatureStatus.Proposed;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReleaseRequirement> Requirements { get; set; } = [];
    public FeatureSlice? Slice { get; set; }
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
    public Guid ReleaseId { get; set; }
    public DevTeamRelease Release { get; set; } = null!;
    public Guid? SessionId { get; set; }
    public DevTeamSession? Session { get; set; }
    public string StageName { get; set; } = string.Empty;
    public ReleaseStageStatus Status { get; set; } = ReleaseStageStatus.Pending;
    public StagePhase Phase { get; set; } = StagePhase.GuidedQA;
    public int QuestionCount { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Summary { get; set; }
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
    public Guid ReleaseId { get; set; }
    public DevTeamRelease Release { get; set; } = null!;
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
    public Guid ReleaseId { get; set; }
    public DevTeamRelease Release { get; set; } = null!;
    public int CurrentStageIndex { get; set; }
    public string CurrentStageName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}