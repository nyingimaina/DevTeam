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
    // A stage's own EntryGates failed before its turn ever started — distinct from
    // BlockedGate, which means *this* stage's own exit-side gates failed after it ran.
    // Retryable once whatever the entry gate checks (usually the previous stage's output)
    // is fixed, same affordance pattern as Escalated/BlockedGate.
    BlockedEntry,
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
    // The agent accepted the prompt but then went completely silent — see
    // BrokerCoordinator's stall watchdog and AcpStalledException. Distinct from TimedOut (the
    // overall request budget) because it fires much sooner and means "the stream stopped".
    Stalled,
    // The model provider refused the request (rate limit, unavailable endpoint, unknown model).
    // Detected from the agent's own log, because opencode never reports it over ACP — without
    // this the turn just hangs until the stall watchdog fires.
    ProviderUnavailable,
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
    /// True for the lightweight release-shell a hotfix (Part 7F) is built on top of — reuses
    /// the exact same ReleaseFeature/pipeline/signoff/gate machinery as a real release (no new
    /// pipeline logic needed), but BranchName is "main" (not "release/&lt;key&gt;") since a
    /// hotfix's single feature merges straight into main on signoff, and it's excluded from
    /// ListReleasesAsync so it never clutters the normal release picker.
    /// </summary>
    public bool IsHotfix { get; set; }
    /// <summary>
    /// Persisted "Continue automatically" state (CruiseControl, front-end) — when true, the
    /// release's active feature keeps auto-advancing through non-interactive stages and
    /// auto-approving signoffs after a reload, instead of resetting to a manual click every
    /// time the page is revisited. MVP: per-release, not per-feature; auto-advance still stops
    /// on its own for the BA's interactive conversation, a genuine blocker, or once the current
    /// feature reaches Ready — it does not auto-start a release's next feature.
    /// </summary>
    public bool AutonomousEnabled { get; set; }
    /// <summary>
    /// The feature currently checked out in this release's workspace, if any — never a real
    /// column. A release has no disk representation of its own; "what's checked out" is a
    /// fact about the *workspace* (see WorkspaceActiveCheckout), since two releases can
    /// legitimately share one WorkspacePath and each would otherwise stomp the other's
    /// checkout state. Callers that load a DevTeamRelease for return (e.g.
    /// WorkflowEngine.LoadReleaseAsync) are responsible for populating this from
    /// WorkspaceActiveCheckout, filtered to a feature that actually belongs to this release —
    /// everything below this point (StageRuns/Signoffs/FlowPosition, the pre-GitFlow wire
    /// shape) is unchanged and keeps working exactly as before once this is set.
    /// </summary>
    [NotMapped]
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

    /// <summary>
    /// What to show and gate on. <see cref="Status"/> is stored and can go stale (a release
    /// stays "Ready" after a new feature is added); this is derived from the features each time.
    /// </summary>
    [NotMapped]
    public ReleaseStatus EffectiveStatus => Workflow.ReleaseStatusRules.Effective(Status, Features.Select(f => f.Status).ToList());
}

/// <summary>
/// The single source of truth for "which branch is physically checked out in this workspace
/// right now" — a fact about the workspace folder, not about any one release, since two
/// releases can share a WorkspacePath. ActiveReleaseFeatureId is the checked-out
/// ReleaseFeature's id regardless of whether it's a normal feature or a hotfix (Part 7F reuses
/// ReleaseFeature for hotfixes too — see DevTeamRelease.IsHotfix); ActiveHotfixId mirrors that
/// same id only when it's a hotfix (null otherwise) — a same-value tag, not a separate id
/// space — purely so a caller can tell which kind is active without an extra join.
/// </summary>
public sealed class WorkspaceActiveCheckout
{
    public string WorkspacePath { get; set; } = string.Empty;
    public Guid? ActiveReleaseFeatureId { get; set; }
    public Guid? ActiveHotfixId { get; set; }
}

/// <summary>
/// A record of one LLM-assisted conflict-resolution attempt (Part 7D) — mirrors
/// SpecialistConsultation's shape (which files, what happened, succeeded y/n) so a resolution
/// is visible for diagnostics instead of disappearing into a session transcript nobody sees
/// afterward. Not tied to a StageRun — a conflict happens outside any specific pipeline stage
/// (a feature-to-release merge, a release-to-main/develop merge during finalization, or a
/// stash-apply during a feature switch) — so it's scoped by a bare SubjectId (a ReleaseFeature
/// or DevTeamRelease id depending on which kind of merge conflicted) with no navigation
/// property; this is a diagnostics log, not something the rest of the domain model needs to
/// traverse.
/// </summary>
public sealed class MergeConflictResolution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubjectId { get; set; }
    public string ConflictedFiles { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public bool Succeeded { get; set; }
    public string ResponseText { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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

    // ── same-stage failure loop guard ──
    // How many times in a row this stage has failed the *same* checks (see GateFailureLoopGuard).
    // Past the cap we stop auto-retrying and ask the human, instead of bouncing forever.
    public int ConsecutiveFailures { get; set; }
    public string? LastFailureSignature { get; set; }
    public bool AutoRetrySuppressed { get; set; }

    /// <summary>
    /// Eagerly-persisted progress marker (a serialized <see cref="StageCheckpoint"/>) written
    /// before/after every agent turn and gate step. Lets the startup crash-recoverer tell a
    /// turn that finished from one that was mid-flight when the broker died.
    /// </summary>
    public string? CheckpointJson { get; set; }

    public List<ReleaseGateCheck> GateChecks { get; set; } = [];
    public List<ReviewFinding> Findings { get; set; } = [];
    public List<ReleaseGuidanceNote> GuidanceNotes { get; set; } = [];
    public List<SpecialistConsultation> SpecialistConsultations { get; set; } = [];
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
    // True when this check ran before the stage's own turn started (WorkflowRole.EntryGates)
    // rather than after it (the existing exit-side Steps) — lets the diagnostics pane group
    // "entry" vs "exit" checks instead of conflating them.
    public bool IsEntryGate { get; set; }
    // Who should act if this specific check fails — see WorkflowStep.ResponsibleRole. Null
    // means the stage that ran it owns the fix (today's implicit default).
    public string? ResponsibleRole { get; set; }

    // Read-only display text for the UI (get-only, so EF doesn't map them): the internal check
    // name and raw evidence stay as the source of truth; users read these instead.
    public string DisplayTitle => Workflow.GateFriendlyText.Describe(Name, EvidenceText).Title;
    public string PlainProblem => Workflow.GateFriendlyText.Describe(Name, EvidenceText).WhatWentWrong;
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

    // ── Negotiation ledger (a finding doubles as one "point" in a push-back round) ──
    // Which round of the negotiation opened this point (1-based). Bounded by
    // NegotiationProtocol.MaxRounds so a stage ping-pong can't run forever.
    public int Round { get; set; } = 1;

    // Who opened it: "system:gate-failure", "user", "challenge:<producer>-><antagonist>", …
    public string? OpenedBy { get; set; }

    // The stage the point was sent back to, so the receiving stage can be handed only its own
    // open points (rather than every note the feature ever accumulated).
    public string? PushedBackTo { get; set; }

    // The concrete, observable thing that closes the point — the "what to check" half of the ask.
    public string? Expected { get; set; }

    // The receiving stage's answer for this point, captured from its completion message.
    public string? ResponseText { get; set; }
    public ReviewFindingResponse ResponseKind { get; set; } = ReviewFindingResponse.None;
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>How the receiving stage answered a negotiation point.</summary>
public enum ReviewFindingResponse
{
    /// <summary>No answer captured yet (the stage hasn't run, or didn't address this point).</summary>
    None,
    Addressed,
    /// <summary>The stage believes the point/check is wrong — routed to the user, never looped.</summary>
    Disputed,
    /// <summary>The stage cannot satisfy it — routed to the business analyst for re-scoping.</summary>
    Blocked,
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