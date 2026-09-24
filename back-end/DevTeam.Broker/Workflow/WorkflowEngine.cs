using System.Text;

using DevTeam.Broker.Context;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
using DevTeam.Broker.Notifications;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Workflow;

public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly IGateRunner _gateRunner;
    private readonly IWorkflowCoordinator _coordinator;
    private readonly IEventBroadcaster _broadcaster;
    private readonly IGitService _gitService;
    private readonly IGitCredentialStore _credentialStore;
    private readonly ActiveTurnTracker _turnTracker;
    private readonly IUserNotifier? _userNotifier;
    private readonly IRepoContextService? _repoContext;
    private readonly WorkflowDefinitionLoader _loader;
    private readonly ILogger<WorkflowEngine> _logger;
    private readonly ModelCatalogService _modelCatalog;

    // Agent prompts run on a dedicated long-lived budget so an aborted HTTP
    // request (or client disconnect) can never kill an in-flight opencode turn.
    // Used for autonomous work (RunStageAsync, challenges) where there's no one
    // waiting live and a long dev/test cycle is expected.
    private static readonly TimeSpan AgentPromptTimeout = TimeSpan.FromMinutes(30);

    // Interactive turns (the user is watching a chat panel right now) get a much
    // shorter budget: a genuinely stuck turn should surface as "something's wrong"
    // within minutes, not 30 of them. The broker also exposes a manual cancel
    // (BrokerCoordinator.CancelCurrentTurnAsync / POST /api/turns/current/cancel)
    // for a turn that's legitimately just slow and needs more time.
    private static readonly TimeSpan InteractiveTurnTimeout = TimeSpan.FromMinutes(5);

    // Without this, a role's agent has no self-knowledge of DevTeam's own orchestration:
    // if asked how work reaches the next role, it has nothing to go on but its own
    // guesswork and can confidently assert the next role/agent isn't configured — even
    // though the handoff (context.md/manifest.yaml bundle + automatic session start) is
    // already fully wired in BuildArtifactContext/StartStageAsync/RunStageAsync.
    private const string HandoffAutomationClause =
        " DevTeam automatically starts the next role's agent session once this stage's gates " +
        "and any required signoff are approved — you do not need to know how the next role is " +
        "invoked, and you must never tell the user their opencode config or repo is missing a " +
        "role or agent. If asked how to proceed, tell them to use the stage's signoff controls in the UI.";

    // Autonomous stages have no one watching their output live — unlike an interactive stage's
    // chat, where a human reads every turn — so tokens spent narrating routine actions are
    // wasted. Only used for the autonomous prompt (RunStageAsync), never the interactive one.
    private const string AutonomousTersenessClause =
        " Bear in mind no one is watching this stage's output live, so keep output terse — skip " +
        "narrating routine actions. Only surface something prominently before your final DONE " +
        "if it's a genuine blocker, an ambiguous decision you had to make, or something the user truly needs to review.";

    // Leaving modelId null lets opencode fall back to its own ambient "current" model,
    // which can silently drift to something far slower than intended (observed: an
    // unpinned session took 20+ minutes per turn vs. ~15s pinned to this model).
    // Always request a known-good default explicitly instead.
    private const string DefaultModelId = "opencode/big-pickle";

    /// <summary>
    /// The model a new stage session should use: whatever the person last chose for this
    /// workspace, falling back to the pinned default. Without this, every stage started on the
    /// default again and the user had to re-pick their model on every single step.
    /// </summary>
    private async Task<string> ResolveModelIdAsync(string workspacePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var preferred = await db.WorkspaceModelSettings
            .Where(s => s.WorkspacePath == workspacePath)
            .Select(s => s.ModelId)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(preferred) ? DefaultModelId : preferred;
    }

    // A confused agent could otherwise ping-pong delegation requests forever — same shape as
    // WorkflowStep.LoopAttempts, just for a different kind of retry.
    private const int MaxDelegationRoundTrips = 3;

    // Same shape as MaxDelegationRoundTrips — caps how many times ResolveConflictAsync (7D)
    // opens a fresh session to try resolving a merge/stash-apply conflict before giving up and
    // surfacing a clear "needs manual resolution" failure instead of retrying forever.
    private const int MaxConflictResolutionAttempts = 3;

    private static CancellationTokenSource AgentPromptCts()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(AgentPromptTimeout);
        return cts;
    }

    private static CancellationTokenSource InteractiveTurnCts()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(InteractiveTurnTimeout);
        return cts;
    }

    public WorkflowEngine(
        IDbContextFactory<DevTeamDbContext> dbFactory,
        IGateRunner gateRunner,
        IWorkflowCoordinator coordinator,
        IEventBroadcaster broadcaster,
        IGitService gitService,
        WorkflowDefinitionLoader loader,
        ILogger<WorkflowEngine> logger,
        ModelCatalogService modelCatalog,
        IGitCredentialStore credentialStore,
        ActiveTurnTracker turnTracker,
        IUserNotifier? userNotifier = null,
        IRepoContextService? repoContext = null)
    {
        _dbFactory = dbFactory;
        _gateRunner = gateRunner;
        _coordinator = coordinator;
        _broadcaster = broadcaster;
        _gitService = gitService;
        _loader = loader;
        _logger = logger;
        _modelCatalog = modelCatalog;
        _credentialStore = credentialStore;
        _turnTracker = turnTracker;
        _userNotifier = userNotifier;
        _repoContext = repoContext;
    }

    // Raises a desktop notification for a stage that reached a terminal state, so someone who
    // walked away learns that work finished or needs them. Failures are swallowed: a notification
    // problem must never affect the work it reports on.
    private async Task NotifyStageFinishedAsync(WorkflowRole role, string featureKey, ReleaseStageRun stageRun, bool allPassed, Guid? featureId = null)
    {
        if (_userNotifier is null)
            return;

        try
        {
            await _userNotifier.StageFinishedAsync(
                new StageOutcome(featureKey, role.Name, stageRun.Status.ToString(), allPassed, featureId),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notification for stage {Stage} failed.", role.Name);
        }
    }

    public async Task<DevTeamRelease> StartReleaseAsync(string featureKey, string workspacePath, CancellationToken ct)
    {
        var workflow = LoadWorkflow(workspacePath);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var releaseBranch = $"release/{featureKey}";
        var featureBranch = $"feature/{featureKey}";

        var release = new DevTeamRelease
        {
            WorkspacePath = workspacePath,
            Title = $"Release {featureKey}",
            Status = ReleaseStatus.InProgress,
            BranchName = releaseBranch,
        };

        var feature = new ReleaseFeature
        {
            Release = release,
            Key = featureKey,
            Title = featureKey,
            BranchName = featureBranch,
            Status = ReleaseFeatureStatus.InProgress,
        };
        release.Features.Add(feature);

        foreach (var role in workflow.Pipeline)
        {
            if (role.Signoff is not null)
            {
                feature.Signoffs.Add(new ReleaseSignoff
                {
                    Feature = feature,
                    StageName = role.Name,
                    Required = true,
                    Approved = false,
                });
            }
        }

        db.Releases.Add(release);
        await db.SaveChangesAsync(ct);

        // The workspace may already have another release's feature checked out (releases have
        // no disk representation of their own — WorkspacePath just tells you where a release's
        // features live, and nothing stops two releases from sharing one). Step it down before
        // taking over the checkout, same as CreateFeatureAsync does within a single release.
        await MarkPreviouslyActiveFeatureOnHoldAsync(db, workspacePath, ct);
        await SetActiveCheckoutAsync(db, workspacePath, releaseFeatureId: feature.Id, isHotfix: false, ct);
        release.CurrentFeatureId = feature.Id;

        var position = new ReleaseFlowPosition
        {
            Feature = feature,
            CurrentStageIndex = 0,
            CurrentStageName = workflow.Pipeline[0].Name,
        };
        db.ReleaseFlowPositions.Add(position);
        await db.SaveChangesAsync(ct);

        feature.FlowPosition = position;

        try
        {
            await _gitService.InitAsync(workspacePath, ct);
            await _gitService.EnsureBranchAsync(workspacePath, releaseBranch, ct);
            await _gitService.EnsureBranchAsync(workspacePath, featureBranch, ct);
            _logger.LogInformation("Created git branches {ReleaseBranch}/{FeatureBranch} for release {ReleaseId}",
                releaseBranch, featureBranch, release.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create git branches for release {ReleaseId}", release.Id);
        }

        _logger.LogInformation("Started release {ReleaseId} for feature {FeatureKey}", release.Id, featureKey);
        return release;
    }

    // ─── hotfixes (Part 7F) ─────────────────────────────────────────────────

    /// <summary>
    /// A hotfix branches from main directly and isn't tied to any in-progress release — but
    /// reuses ReleaseFeature/the pipeline/signoff/gate machinery wholesale (see
    /// DevTeamRelease.IsHotfix) instead of a parallel set of tables, so it runs through the
    /// exact same RunStageAsync/StartStageAsync/SignoffAsync flow as a normal feature with no
    /// new pipeline logic: on last-stage signoff, FinalizeFeatureCompletionAsync already merges
    /// the hotfix branch into release.BranchName ("main" here) and pushes/deletes it — the only
    /// genuinely new step is FinalizeHotfixAsync's follow-up merge into develop.
    /// </summary>
    public async Task<ReleaseFeature> StartHotfixAsync(string key, string workspacePath, CancellationToken ct)
    {
        var workflow = LoadWorkflow(workspacePath);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var hotfixBranch = $"hotfix/{key}";

        var release = new DevTeamRelease
        {
            WorkspacePath = workspacePath,
            Title = $"Hotfix {key}",
            Status = ReleaseStatus.InProgress,
            BranchName = "main",
            IsHotfix = true,
        };

        var hotfix = new ReleaseFeature
        {
            Release = release,
            Key = key,
            Title = key,
            BranchName = hotfixBranch,
            Status = ReleaseFeatureStatus.InProgress,
        };
        release.Features.Add(hotfix);

        foreach (var role in workflow.Pipeline)
        {
            if (role.Signoff is not null)
            {
                hotfix.Signoffs.Add(new ReleaseSignoff
                {
                    Feature = hotfix,
                    StageName = role.Name,
                    Required = true,
                    Approved = false,
                });
            }
        }

        db.Releases.Add(release);
        await db.SaveChangesAsync(ct);

        await MarkPreviouslyActiveFeatureOnHoldAsync(db, workspacePath, ct);
        await SetActiveCheckoutAsync(db, workspacePath, releaseFeatureId: hotfix.Id, isHotfix: true, ct);

        var position = new ReleaseFlowPosition
        {
            Feature = hotfix,
            CurrentStageIndex = 0,
            CurrentStageName = workflow.Pipeline[0].Name,
        };
        db.ReleaseFlowPositions.Add(position);
        await db.SaveChangesAsync(ct);

        hotfix.FlowPosition = position;

        try
        {
            var checkoutMain = await _gitService.CheckoutAsync(workspacePath, "main", ct);
            if (!checkoutMain.Success)
                _logger.LogWarning("Failed to check out 'main' for hotfix {HotfixId}: {Message}", hotfix.Id, checkoutMain.Message);
            await _gitService.EnsureBranchAsync(workspacePath, hotfixBranch, ct);
            _logger.LogInformation("Created git branch {Branch} for hotfix {HotfixId}", hotfixBranch, hotfix.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create git branch for hotfix {HotfixId}", hotfix.Id);
        }

        return hotfix;
    }

    /// <summary>
    /// The hotfix's own pipeline completion (SignoffAsync -> FinalizeFeatureCompletionAsync)
    /// already merged it into main, pushed, and deleted its branch — this is the explicit
    /// follow-up that additionally merges main into develop, so the fix isn't lost when the
    /// next release cuts from develop, then marks the hotfix's release-shell Released. Reuses
    /// MergeReleaseIntoAsync verbatim: release.BranchName is "main" for a hotfix, so merging
    /// "release.BranchName -> develop" is exactly "main -> develop".
    /// </summary>
    public async Task<DevTeamRelease> FinalizeHotfixAsync(Guid hotfixId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var hotfix = await db.ReleaseFeatures.Include(f => f.Release)
            .SingleOrDefaultAsync(f => f.Id == hotfixId, ct)
            ?? throw new KeyNotFoundException($"Hotfix {hotfixId} not found.");

        var release = hotfix.Release;
        if (!release.IsHotfix)
            throw new InvalidOperationException($"{hotfixId} is not a hotfix.");
        if (hotfix.Status != ReleaseFeatureStatus.Complete)
            throw new InvalidOperationException(
                $"The hotfix must complete its pipeline (signed off into main) before finalizing (current status: {hotfix.Status}).");
        if (release.Status == ReleaseStatus.Released)
            throw new InvalidOperationException("This hotfix has already been finalized.");

        await MergeReleaseIntoAsync(db, release, "develop", ct);

        var authToken = await TryGetCredentialForWorkspaceAsync(db, release.WorkspacePath, ct);
        var remoteCheck = await _gitService.HasRemoteAsync(release.WorkspacePath, ct);
        if (remoteCheck.HasRemote)
        {
            var pushDevelop = await _gitService.PushAsync(release.WorkspacePath, "develop", authToken, ct);
            if (!pushDevelop.Success)
                _logger.LogWarning("Push of 'develop' failed after finalizing hotfix '{Hotfix}': {Message}", hotfix.Key, pushDevelop.Message);
        }
        else
        {
            _logger.LogInformation("No remote configured for '{Workspace}'; skipping push after finalizing hotfix.", release.WorkspacePath);
        }

        release.Status = ReleaseStatus.Released;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return await LoadReleaseAsync(db, release.Id, ct);
    }

    public async Task<IReadOnlyList<DevTeamRelease>> ListHotfixesAsync(string? workspacePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hotfixes = await db.Releases
            .Where(r => r.IsHotfix)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns)
            .Include(r => r.Features).ThenInclude(f => f.Signoffs)
            .Include(r => r.Features).ThenInclude(f => f.FlowPosition)
            .ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(workspacePath))
            hotfixes = hotfixes.Where(r => IsSameWorkspace(r.WorkspacePath, workspacePath)).ToList();

        return hotfixes.OrderByDescending(r => r.CreatedAt).ToList();
    }

    // ─── feature lifecycle (GitFlow) ────────────────────────────────────────

    public async Task<ReleaseFeature> CreateFeatureAsync(Guid releaseId, string featureKey, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await db.Releases
            .Include(r => r.Features)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        var workflow = LoadWorkflow(release.WorkspacePath);

        var featureBranch = $"feature/{featureKey}";
        var feature = new ReleaseFeature
        {
            ReleaseId = release.Id,
            Release = release,
            Key = featureKey,
            Title = featureKey,
            BranchName = featureBranch,
            Status = ReleaseFeatureStatus.InProgress,
        };

        foreach (var role in workflow.Pipeline)
        {
            if (role.Signoff is not null)
            {
                feature.Signoffs.Add(new ReleaseSignoff
                {
                    Feature = feature,
                    StageName = role.Name,
                    Required = true,
                    Approved = false,
                });
            }
        }

        db.ReleaseFeatures.Add(feature);
        await db.SaveChangesAsync(ct);

        var position = new ReleaseFlowPosition
        {
            Feature = feature,
            CurrentStageIndex = 0,
            CurrentStageName = workflow.Pipeline[0].Name,
        };
        db.ReleaseFlowPositions.Add(position);
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Creating a feature immediately checks it out (today's UX, unchanged), displacing
        // whatever was previously active in this workspace — InProgress means "the one
        // currently checked out," so whatever's displaced steps down to OnHold rather than
        // two features both reading InProgress at once.
        await MarkPreviouslyActiveFeatureOnHoldAsync(db, release.WorkspacePath, ct);
        await SetActiveCheckoutAsync(db, release.WorkspacePath, releaseFeatureId: feature.Id, isHotfix: false, ct);

        feature.FlowPosition = position;

        try
        {
            await _gitService.CheckoutAsync(release.WorkspacePath, release.BranchName, ct);
            await _gitService.EnsureBranchAsync(release.WorkspacePath, featureBranch, ct);
            _logger.LogInformation("Created git branch {Branch} for feature {FeatureId}", featureBranch, feature.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create git branch for feature {FeatureId}", feature.Id);
        }

        return feature;
    }

    /// <summary>
    /// Switches this workspace's checkout to a different, already-existing feature — parking
    /// whatever was active (stashing its WIP if the tree is dirty) and restoring the target's
    /// own parked WIP, if any. Two releases can share a WorkspacePath, so this only ever looks
    /// at WorkspaceActiveCheckout (a workspace-level fact), never a release's own state.
    /// </summary>
    public async Task<DevTeamRelease> SwitchFeatureAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await db.ReleaseFeatures
            .Include(f => f.Release)
            .SingleOrDefaultAsync(f => f.Id == featureId, ct)
            ?? throw new KeyNotFoundException($"Feature {featureId} not found.");

        var workspacePath = feature.Release.WorkspacePath;

        // Don't check out from under an in-flight agent turn in this same workspace. A turn
        // running in an unrelated workspace shouldn't block this switch — the global
        // single-turn lock (BrokerCoordinator) already rules out any real file-write race;
        // this is specifically about not yanking the branch out from under a turn actively
        // using it right now.
        var activeTurn = _turnTracker.Current;
        if (activeTurn is not null)
        {
            var activeSession = await db.Sessions.FindAsync([activeTurn.SessionId], ct);
            if (activeSession is not null && activeSession.WorkspacePath == workspacePath)
                throw new InvalidOperationException(
                    "A stage is currently running in this workspace. Wait for it to finish, or cancel it, before switching.");
        }

        var checkout = await db.WorkspaceActiveCheckouts.FindAsync([workspacePath], ct);
        if (checkout?.ActiveReleaseFeatureId is { } outgoingFeatureId && outgoingFeatureId != featureId)
        {
            var status = await _gitService.StatusAsync(workspacePath, ct);
            if (!status.IsClean)
            {
                var stashResult = await _gitService.StashPushAsync(workspacePath, StashTagForFeature(outgoingFeatureId), ct);
                if (!stashResult.Success)
                    throw new InvalidOperationException($"Could not stash the outgoing feature's changes: {stashResult.Message}");
            }
        }
        await MarkPreviouslyActiveFeatureOnHoldAsync(db, workspacePath, ct);

        var checkoutResult = await _gitService.CheckoutAsync(workspacePath, feature.BranchName, ct);
        if (!checkoutResult.Success)
            throw new InvalidOperationException($"Could not check out '{feature.BranchName}': {checkoutResult.Message}");

        var targetTag = StashTagForFeature(featureId);
        var stashList = await _gitService.StashListAsync(workspacePath, ct);
        if (stashList.StashEntries?.Any(e => e.Contains(targetTag, StringComparison.Ordinal)) == true)
        {
            var applyResult = await _gitService.StashApplyAsync(workspacePath, targetTag, ct);
            if (!applyResult.Success)
            {
                var conflictedFiles = applyResult.ConflictedFiles ?? [];
                var prefixes = ResolveAllowedWritePrefixes(writesCode: true, "conflict-resolver", workspacePath, feature.Key, LoadWorkflow(workspacePath));
                var resolved = conflictedFiles.Length > 0 &&
                    await ResolveConflictAsync(featureId, workspacePath, prefixes, conflictedFiles, db, ct);
                if (!resolved)
                {
                    // The stash is left in place (never dropped on failure) so a human or a
                    // later retry can still resolve it — never silently lost or discarded.
                    throw new InvalidOperationException(
                        $"Restoring '{feature.Key}'s parked changes failed: {applyResult.Message}. " +
                        "The stash was not dropped — resolve the conflict manually, then retry.");
                }
            }
            await _gitService.StashDropAsync(workspacePath, targetTag, ct);
        }

        feature.Status = ReleaseFeatureStatus.InProgress;
        await SetActiveCheckoutAsync(db, workspacePath, releaseFeatureId: featureId, isHotfix: feature.Release.IsHotfix, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    private static string StashTagForFeature(Guid featureId) => $"devteam-feature-{featureId:N}";

    // ─── interactive stage lifecycle ───────────────────────────────────────

    public async Task<ReleaseStageRun> StartStageAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var currentStageIndex = position.CurrentStageIndex;
        if (currentStageIndex >= workflow.Pipeline.Count)
            throw new InvalidOperationException("All stages already complete.");

        var role = workflow.Pipeline[currentStageIndex];

        // AcpSessionId is required here, not just Status == Active: RetryStageAsync inserts a
        // fresh Active row with no session yet (its whole point is to replace a stuck/orphaned
        // one), and without this check that row satisfies "already active" and gets handed back
        // as-is — no new session ever opens, so the retry silently does nothing.
        var existingActive = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active
                && sr.AcpSessionId is not null);
        if (existingActive is not null)
            return existingActive;

        var featureKey = feature.Key;
        var workspacePath = feature.Release.WorkspacePath;

        // RetryStageAsync may already have inserted a fresh Active/sessionless row for this
        // stage (see the comment above) — reuse it instead of adding another one alongside it.
        var pendingRetry = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active
                && sr.AcpSessionId is null);

        var stageRun = pendingRetry ?? new ReleaseStageRun
        {
            Feature = feature,
            StageName = role.Name,
            Attempt = feature.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.GuidedQA,
        };
        if (pendingRetry is null)
            db.ReleaseStageRuns.Add(stageRun);
        SetCheckpoint(stageRun, StageCheckpointSignal.Idle);
        await db.SaveChangesAsync(ct);

        // Entry gates run before a session is even opened — a role that never gets past its
        // own entry gates has no need for one yet, and shouldn't pay for it.
        if (!await RunEntryGatesAsync(role, featureKey, workspacePath, workflow, stageRun, db, ct))
            return stageRun;

        var allowedWritePrefixes = ResolveAllowedWritePrefixes(role.WritesCode, role.Name, workspacePath, featureKey, workflow);
        var session = await _coordinator.NewSessionAsync(workspacePath, await ResolveModelIdAsync(workspacePath, ct), allowedWritePrefixes, ct);
        stageRun.AcpSessionId = session.SessionId.ToString();
        await db.SaveChangesAsync(ct);

        // Run this role's leading builtins (e.g. scaffold_specs/context_bundle for
        // business-analyst) before the very first chat message goes out, so the agent
        // starts with a scaffold already in place instead of discovering it mid-conversation.
        await RunLeadingBuiltinsAsync(role, featureKey, workspacePath, workflow, stageRun, db, ct);
        await db.SaveChangesAsync(ct);

        var guidanceContext = BuildGuidanceContext(feature);
        var artifactContext = BuildArtifactContext(workspacePath, featureKey, currentStageIndex == 0);
        var resolvedPrompt = await ResolveActiveProfilePromptAsync(db, workspacePath, role.Name, ct);
        var profilePromptText = ResolvePlaceholders(resolvedPrompt.Text, workflow, workspacePath, featureKey);
        var specialists = await db.SpecialistRoles.ToListAsync(ct);

        // A role-declared seed (e.g. the default business-analyst role's BRS-authoring
        // instruction — see WorkflowYaml.DefaultPipeline) instead of a name check, so a
        // custom/renamed first stage doesn't silently inherit BA-specific instructions it
        // never asked for.
        var requirementsAuthoring = role.SeedPrompt is null
            ? string.Empty
            : ResolvePlaceholders(role.SeedPrompt, workflow, workspacePath, featureKey);

        var baseInstruction = $"You are the {role.Name} for feature '{featureKey}' in workspace '{workspacePath}'. " +
            MultiQuestionDetector.SingleQuestionInstruction + " " +
            MultiQuestionDetector.QuickReplyFormattingInstruction +
            " Follow each answer to its logical conclusion before asking the next. " +
            "When you have enough information to produce the required output, say DONE and provide the structured result.";
        var delegationClause = BuildDelegationClause(specialists, featureKey);
        var profileClause = AsAugmentingClause(profilePromptText);
        var pointsContext = BuildNegotiationContext(feature, role.Name);

        var finalPrompt = resolvedPrompt.OverridesBuiltIn
            ? profilePromptText
            : baseInstruction + requirementsAuthoring + HandoffAutomationClause + delegationClause +
              profileClause + guidanceContext + pointsContext + artifactContext;

        // What the prompt is made of — so a bloated opening prompt can be traced to a section.
        var composition = resolvedPrompt.OverridesBuiltIn
            ? new PromptComposition(finalPrompt.Length, 0, 0, 0, 0, 0, 0, 0, InterviewChars(workspacePath, featureKey))
            : new PromptComposition(
                baseInstruction.Length, requirementsAuthoring.Length, profileClause.Length,
                HandoffAutomationClause.Length, delegationClause.Length, guidanceContext.Length,
                pointsContext.Length, artifactContext.Length, InterviewChars(workspacePath, featureKey));

        // Use a separate cancellation for the agent prompt so HTTP timeouts don't kill it.
        // The agent may take time to process the initial prompt — that's expected.
        using var agentCts = InteractiveTurnCts();
        // Eager on-disk checkpoint: "prompt in flight" before dispatch and "prompt done" after
        // — a broker crash between the two is exactly the "session died mid-turn" case the
        // startup reconciler escalates.
        SetCheckpoint(stageRun, StageCheckpointSignal.PromptInProgress);
        await db.SaveChangesAsync(ct);
        try
        {
            var promptResponse = await PromptWithDelegationAsync(session.SessionId, finalPrompt, stageRun, workspacePath, featureKey, workflow, db, agentCts.Token);
            await RecordTurnMetricAsync(
                db, stageRun, featureKey, workspacePath, role.Name, TurnKind.Stage,
                await ResolveModelIdAsync(workspacePath, agentCts.Token),
                finalPrompt.Length, composition.ToJson(), promptResponse, agentCts.Token);
            SetCheckpoint(stageRun, StageCheckpointSignal.PromptSucceeded);
            ClearPromptFailure(stageRun);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelling the first turn of an interactive stage is not an agent failure.
            MarkPromptCancelled(stageRun);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Not ct: by the time a minutes-long agent turn fails, the caller (an HTTP request)
            // may well have already disconnected and cancelled ct — recording the failure must
            // still go through, or the stage is left looking "Active" forever with no error.
            await RecordPromptFailureAsync(db, stageRun, ex, CancellationToken.None);
            throw;
        }

        _logger.LogInformation("Started stage {StageName} for feature {FeatureId}, session {SessionId}",
            role.Name, featureId, session.SessionId);
        return stageRun;
    }

    public async Task<StagePromptResult> SendMessageAsync(Guid featureId, string text, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var role = workflow.Pipeline[position.CurrentStageIndex];
        // Escalated (Part 1) means this stage's last turn failed, not that it's no longer
        // the live one — a retry message still needs to reach it, so it's treated the same
        // as Active for the purposes of "which stage run does this message target".
        var stageRun = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name &&
                (sr.Status == ReleaseStageStatus.Active || sr.Status == ReleaseStageStatus.Escalated))
            ?? throw new InvalidOperationException($"No active stage run for '{role.Name}'.");

        if (stageRun.AcpSessionId is null)
            throw new InvalidOperationException("Stage run has no linked session.");

        var acpSessionId = Guid.Parse(stageRun.AcpSessionId);

        // A new human message always invalidates a prior "ready to proceed" signal — the
        // conversation isn't over until the agent says DONE again and gates re-pass.
        stageRun.ReadyToProceed = false;

        // Eager checkpoint: the run is about to be mid-prompt — a crash from here on means the
        // turn, and by extension the session, is lost until the reconciler surfaces it.
        SetCheckpoint(stageRun, StageCheckpointSignal.PromptInProgress);
        await db.SaveChangesAsync(ct);

        // Re-state the one-question rule on every turn, not just the opening prompt: a chat model
        // drifts, and each drifting turn costs a correction round-trip. The rule is appended for
        // the model only — displayText keeps the user's chat bubble showing exactly what they typed.
        var modelText = role.UserInputRequired
            ? text + " " + MultiQuestionDetector.SingleQuestionInstruction + " " + MultiQuestionDetector.QuickReplyFormattingInstruction
            : text;

        using var agentCts = InteractiveTurnCts();
        PromptResponse response;
        try
        {
            response = await _coordinator.PromptWithSessionRecoveryAsync(
                acpSessionId, modelText, agentCts.Token, displayText: text);
            SetCheckpoint(stageRun, StageCheckpointSignal.PromptSucceeded);
            ClearPromptFailure(stageRun);
        }
        catch (OperationCanceledException)
        {
            // Cancelling a chat turn is not an agent failure.
            MarkPromptCancelled(stageRun);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await RecordPromptFailureAsync(db, stageRun, ex, agentCts.Token);
            throw;
        }

        stageRun.QuestionCount++;
        await db.SaveChangesAsync(agentCts.Token);

        // Persist this turn's Q&A to disk now, so a crash mid-interview can resume from here
        // instead of asking everything again.
        await TryRecordInterviewTurnAsync(db, acpSessionId, feature.Key, feature.Release.WorkspacePath, text, agentCts.Token);
        await RecordTurnMetricAsync(
            db, stageRun, feature.Key, feature.Release.WorkspacePath, role.Name, TurnKind.Stage,
            await ResolveModelIdAsync(feature.Release.WorkspacePath, agentCts.Token),
            modelText.Length, null, response, agentCts.Token);

        return new StagePromptResult(
            Response: response.StopReason,
            InputTokens: response.TotalTokens,
            OutputTokens: response.OutputTokens,
            TotalTokens: response.TotalTokens);
    }

    public async Task<StagePromptResult> SendMessageEnforcingSingleQuestionAsync(Guid featureId, string text, CancellationToken ct)
    {
        var result = await SendMessageAsync(featureId, text, ct);

        using var agentCts = InteractiveTurnCts();
        var token = agentCts.Token;
        await using var db = await _dbFactory.CreateDbContextAsync(token);
        var feature = await LoadFeatureAsync(db, featureId, token);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");
        var role = workflow.Pipeline[position.CurrentStageIndex];
        var stageRun = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active)
            ?? throw new InvalidOperationException($"No active stage run for '{role.Name}'.");

        if (stageRun.AcpSessionId is null)
            return result;

        var acpSessionId = Guid.Parse(stageRun.AcpSessionId);
        var corrections = 0;
        while (corrections < MultiQuestionDetector.MaxCorrections)
        {
            var latest = await GetLatestAssistantTextAsync(db, acpSessionId, token);
            if (!MultiQuestionDetector.ContainsMultipleQuestions(latest))
                break;

            // Quote the offending questions back (see BuildCorrectionPrompt) — the model must not
            // have to guess which message or which question we're objecting to.
            var correctionPrompt = MultiQuestionDetector.BuildCorrectionPrompt(latest);
            var correction = await _coordinator.PromptWithSessionRecoveryAsync(
                acpSessionId, correctionPrompt, token, isPriming: true);
            await RecordTurnMetricAsync(
                db, stageRun, feature.Key, feature.Release.WorkspacePath, role.Name, TurnKind.Correction,
                await ResolveModelIdAsync(feature.Release.WorkspacePath, token),
                correctionPrompt.Length, null, correction, token);
            result = new StagePromptResult(
                Response: correction.StopReason,
                InputTokens: correction.TotalTokens,
                OutputTokens: correction.OutputTokens,
                TotalTokens: correction.TotalTokens);
            corrections++;
        }

        if (corrections > 0)
        {
            _logger.LogInformation(
                "Applied {Corrections} single-question correction(s) for feature {FeatureId}",
                corrections, featureId);
        }

        return result;
    }

    public async Task<DevTeamRelease> RunGatesAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var role = workflow.Pipeline[position.CurrentStageIndex];
        var featureKey = feature.Key;
        var currentIndex = position.CurrentStageIndex;

        // Re-entrancy: gates may be re-run on the same run row when it crashed mid-gates (the
        // startup reconciler heals GatesRunning back to Active) or landed in a retryable
        // failure state (BlockedGate/Escalated/BlockedSignoff/BlockedEntry) — never by opening
        // a phantom second attempt. A run actually still mid-gates is refused: a concurrent
        // pass would execute every step twice against the same state.
        var alreadyRunning = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.GatesRunning);
        if (alreadyRunning is not null)
            throw new InvalidOperationException($"Gates for stage '{role.Name}' are already running.");

        var stageRun = feature.StageRuns
            .Where(sr => sr.StageName == role.Name &&
                sr.Status is ReleaseStageStatus.Active or ReleaseStageStatus.Escalated)
            .OrderByDescending(sr => sr.Attempt)
            .FirstOrDefault()
            ?? feature.StageRuns
                .Where(sr => sr.StageName == role.Name &&
                    sr.Status is ReleaseStageStatus.BlockedGate or ReleaseStageStatus.BlockedSignoff or ReleaseStageStatus.BlockedEntry)
                .OrderByDescending(sr => sr.Attempt)
                .FirstOrDefault()
            ?? throw new InvalidOperationException($"No active stage run for '{role.Name}'.");

        // A retried run restarts from a clean slate on the SAME row — no new attempt, no
        // stale readiness/error/terminal markers carried over.
        if (stageRun.Status is not ReleaseStageStatus.Active and not ReleaseStageStatus.Escalated)
        {
            stageRun.Status = ReleaseStageStatus.Active;
            stageRun.ReadyToProceed = false;
            stageRun.FinishedAt = null;
            stageRun.Summary = null;
            stageRun.LastErrorKind = StageErrorKind.None;
            stageRun.LastErrorMessage = null;
            stageRun.LastErrorAt = null;
        }

        // Check user input requirement
        if (role.UserInputRequired && stageRun.QuestionCount == 0)
        {
            throw new InvalidOperationException(
                $"Stage '{role.Name}' requires user input. Please chat with the agent before running gates.");
        }

        // Check expected artifacts (warn but don't block)
        var workspacePath = feature.Release.WorkspacePath;
        var missingArtifacts = new List<string>();
        foreach (var artifactPattern in role.ExpectedArtifacts)
        {
            var artifactPath = ResolvePlaceholders(artifactPattern, workflow, workspacePath, featureKey);
            var fullPath = Path.Combine(workspacePath, artifactPath);
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                missingArtifacts.Add(artifactPattern);
            }
        }

        if (missingArtifacts.Count > 0)
        {
            _logger.LogWarning("Stage '{Stage}' missing artifacts: {Artifacts}",
                role.Name, string.Join(", ", missingArtifacts));
        }

        stageRun.Phase = StagePhase.Gates;
        stageRun.Status = ReleaseStageStatus.GatesRunning;
        SetCheckpoint(stageRun, StageCheckpointSignal.Gates);
        await db.SaveChangesAsync(ct);

        // Re-entrancy: a retried run carries the exit-side gate-check rows from its prior
        // attempt. Sweep them so re-running gates never accumulates duplicate rows; keep entry
        // gates and the leading builtin checks (scaffold_specs/context_bundle) that already ran
        // at start-stage and must not re-run (ScaffoldSpecsGate refuses to overwrite a scaffold).
        var baselineCheckNames = CollectBaselineCheckNames(role);
        var staleChecks = stageRun.GateChecks
            .Where(gc => !gc.IsEntryGate && !baselineCheckNames.Contains(gc.Name))
            .ToList();
        foreach (var stale in staleChecks)
        {
            stageRun.GateChecks.Remove(stale);
            db.ReleaseGateChecks.Remove(stale);
        }

        // Leading builtins (e.g. scaffold_specs/context_bundle) already ran in StartStageAsync
        // before the chat started, and are already recorded on stageRun.GateChecks. Re-running
        // them here would trip ScaffoldSpecsGate's "don't overwrite an existing scaffold" guard,
        // so skip every step up to and including the first Agent/Loop step, and seed allPassed
        // from those already-recorded results instead of assuming success.
        var allPassed = stageRun.GateChecks.All(gc => gc.Passed);
        var stepResults = new List<CheckpointStepResult>();

        async Task RunAndRecordAsync(WorkflowStep stepToRun)
        {
            var result = await ExecuteStepAsync(stepToRun, role, featureKey, workspacePath, workflow, stageRun, db, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
                StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-result.DurationMs),
                CompletedAt = DateTimeOffset.UtcNow,
            });
            stepResults.Add(new CheckpointStepResult(result.StepName, result.Passed, result.Evidence));
            // Flush the step ledger after EACH step so a crash mid-gates leaves an exact trail
            // of what had already passed when the broker died.
            SetCheckpoint(stageRun, StageCheckpointSignal.Gates, stepResults);
            await db.SaveChangesAsync(ct);
            if (!result.Passed) allPassed = false;
        }

        var pastLeadingSteps = false;
        foreach (var step in role.Steps)
        {
            if (!pastLeadingSteps)
            {
                if (step.Kind is WorkflowStepKind.Agent or WorkflowStepKind.Loop)
                {
                    pastLeadingSteps = true;

                    // A Loop step bundles the agent turn with follow-up checks (e.g. verify_code)
                    // that must still be re-validated on every gates-only recheck — skipping the
                    // whole Loop step here (as "already ran at start-stage") used to mean a
                    // post-repair recheck could go green without the tests ever running again.
                    if (step.Kind == WorkflowStepKind.Loop)
                    {
                        foreach (var innerStep in step.LoopSteps ?? [])
                        {
                            if (innerStep.Kind == WorkflowStepKind.Agent) continue;
                            await RunAndRecordAsync(innerStep);
                        }
                    }
                }
                continue;
            }
            if (step.Kind == WorkflowStepKind.Agent) continue;
            await RunAndRecordAsync(step);
        }

        // Only worth an antagonist-review call when the deterministic gates above already
        // passed — see the identical guard in RunStageAsync.
        if (allPassed)
        {
            stageRun.Phase = StagePhase.Challenge;
            await db.SaveChangesAsync(ct);

            var challenge = workflow.Challenges.FirstOrDefault(c => c.Producer == role.Name);
            if (challenge is not null)
            {
                var challengeResult = await RunChallengeAsync(challenge, stageRun, featureKey, workspacePath, db, ct);
                if (!challengeResult.Passed) allPassed = false;
            }
        }

        // Passing gates is the authoritative "this stage's work is technically complete" signal.
        // Requiring the agent to also say a literal standalone "DONE" line on top of that was too
        // fragile in practice (real replies rarely match that exact shape) and left the human with
        // no way to advance even once the gates had genuinely passed.
        stageRun.ReadyToProceed = allPassed;

        if (allPassed)
            await CommitStageWorkAsync(workspacePath, role.Name, featureKey, ct);

        var isLastStage = false;
        if (allPassed)
        {
            var needsSignoff = feature.Signoffs.Any(s => s.StageName == role.Name && !s.Approved);
            stageRun.Status = needsSignoff ? ReleaseStageStatus.BlockedSignoff : ReleaseStageStatus.Complete;
            stageRun.Phase = StagePhase.Signoff;

            if (!needsSignoff)
            {
                position.CurrentStageIndex++;
                position.CurrentStageName = currentIndex + 1 < workflow.Pipeline.Count
                    ? workflow.Pipeline[currentIndex + 1].Name
                    : "done";
                position.UpdatedAt = DateTimeOffset.UtcNow;

                isLastStage = position.CurrentStageIndex >= workflow.Pipeline.Count;
                feature.Release.Status = isLastStage ? ReleaseStatus.Ready : feature.Release.Status;
            }
            else
            {
                feature.Release.Status = ReleaseStatus.Blocked;
            }
        }
        else
        {
            stageRun.Status = ReleaseStageStatus.BlockedGate;
        }

        stageRun.FinishedAt = DateTimeOffset.UtcNow;
        stageRun.Summary = allPassed
            ? $"Stage {role.Name} passed all gates and challenge."
            : $"Stage {role.Name} failed gates or challenge.";

        await NotifyStageFinishedAsync(role, featureKey, stageRun, allPassed, featureId);

        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (isLastStage)
        {
                await FinalizeFeatureCompletionAndIndexAsync(db, feature, ct);
        }

        await BroadcastEventAsync(feature.ReleaseId, "stageStateChanged", new
        {
            StageName = role.Name,
            Status = stageRun.Status.ToString(),
            Phase = stageRun.Phase.ToString(),
            AllPassed = allPassed,
        }, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    // Lets the agent that just failed its exit gates be talked to again without opening a
    // fresh attempt (which would lose its session/context): SendMessageAsync only targets an
    // Active/Escalated run, so a BlockedGate run is flipped back to Active first. The gate
    // ledger is left untouched — RunGatesAsync sweeps and re-records it on the next pass.
    public async Task<DevTeamRelease> ReopenBlockedGateAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var role = workflow.Pipeline[position.CurrentStageIndex];
        var stageRun = feature.StageRuns
            .Where(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.BlockedGate)
            .OrderByDescending(sr => sr.Attempt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Stage '{role.Name}' has no failed checks to reopen.");

        stageRun.Status = ReleaseStageStatus.Active;
        stageRun.Phase = StagePhase.GuidedQA;
        stageRun.ReadyToProceed = false;
        stageRun.FinishedAt = null;
        stageRun.Summary = null;
        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    // ─── autonomous stage execution ─────────────────────────────────────────

    public async Task<DevTeamRelease> RunStageAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var currentIndex = position.CurrentStageIndex;
        if (currentIndex >= workflow.Pipeline.Count)
            throw new InvalidOperationException("All stages already complete.");

        var role = workflow.Pipeline[currentIndex];
        if (role.UserInputRequired)
        {
            throw new InvalidOperationException(
                $"Stage '{role.Name}' requires user input. " +
                "Use StartStageAsync → SendMessageAsync → RunGatesAsync for interactive stages.");
        }

        var featureKey = feature.Key;
        var workspacePath = feature.Release.WorkspacePath;

        var stageRun = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active);
        if (stageRun is null)
        {
            // A failed run is BlockedGate, so a re-run lands here as a brand-new row. Carry the
            // loop-guard state forward, or every "Run Stage Again" would reset the failure count
            // and the same dead-end check could bounce forever.
            var previousRun = feature.StageRuns
                .Where(sr => sr.StageName == role.Name)
                .OrderByDescending(sr => sr.Attempt)
                .FirstOrDefault();

            stageRun = new ReleaseStageRun
            {
                Feature = feature,
                StageName = role.Name,
                Attempt = feature.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
                Status = ReleaseStageStatus.Active,
                Phase = StagePhase.GuidedQA,
                ConsecutiveFailures = previousRun?.ConsecutiveFailures ?? 0,
                LastFailureSignature = previousRun?.LastFailureSignature,
                AutoRetrySuppressed = previousRun?.AutoRetrySuppressed ?? false,
            };
            db.ReleaseStageRuns.Add(stageRun);
            SetCheckpoint(stageRun, StageCheckpointSignal.Idle);
            await db.SaveChangesAsync(ct);

            // Entry gates run before a session is even opened — a role that never gets past
            // its own entry gates has no need for one yet, and shouldn't pay for it.
            if (!await RunEntryGatesAsync(role, featureKey, workspacePath, workflow, stageRun, db, ct))
            {
                await BroadcastEventAsync(feature.ReleaseId, "stageStateChanged", new
                {
                    StageName = role.Name,
                    Status = stageRun.Status.ToString(),
                    Phase = stageRun.Phase.ToString(),
                    AllPassed = false,
                }, ct);
                return await LoadReleaseAsync(db, feature.ReleaseId, ct);
            }

            var allowedWritePrefixes = ResolveAllowedWritePrefixes(role.WritesCode, role.Name, workspacePath, featureKey, workflow);
            // A purely deterministic role (no Agent/Loop step — e.g. the 'verification' stage)
            // never talks to a model, so it must not pay for — or depend on — an opencode session
            // being available. Its builtin steps still run below via the normal step loop.
            if (RoleUsesAgent(role))
            {
                var session = await _coordinator.NewSessionAsync(workspacePath, await ResolveModelIdAsync(workspacePath, ct), allowedWritePrefixes, ct);
                stageRun.AcpSessionId = session.SessionId.ToString();
                await db.SaveChangesAsync(ct);
            }
        }

        stageRun.Phase = StagePhase.Producing;
        await db.SaveChangesAsync(ct);

        // A re-invoked run (a retry after a cancel/stall, or a second Run click) must not
        // accumulate duplicate step rows — clear this run's own exit-side ledger and let the
        // loop below re-record it, mirroring RunGatesAsync's sweep. Entry gates keep their rows:
        // they ran once, when the run was opened, and are never re-run.
        foreach (var stale in stageRun.GateChecks.Where(gc => !gc.IsEntryGate).ToList())
        {
            stageRun.GateChecks.Remove(stale);
            db.ReleaseGateChecks.Remove(stale);
        }
        await db.SaveChangesAsync(ct);

        // Set when a prompt is cancelled — see PromptRoleAsync's catch and the early return below.
        var cancelled = false;
        // Set when the agent accepted the prompt and then went silent (BrokerCoordinator's stall
        // watchdog). Also unwinds the stage, but escalates rather than leaving it resumable.
        var stalled = false;
        // Set when the model provider itself refused the request — see ProviderUnavailableException.
        var providerRefused = false;

        async Task PromptRoleAsync(int attempt = 1)
        {
            if (stageRun.AcpSessionId is null)
                throw new InvalidOperationException("Stage run has no linked session.");
            var acpSessionId = Guid.Parse(stageRun.AcpSessionId);

            using var agentCts = AgentPromptCts();
            var artifactContext = BuildArtifactContext(workspacePath, featureKey, currentIndex == 0);
            // Read fresh on every call (not captured once before the loop) so a note added
            // after a failed attempt — including the gate-failure feedback below — is seen
            // by the very next retry instead of only by some future, unrelated stage.
            var guidanceContext = BuildGuidanceContext(feature);
            var resolvedPrompt = await ResolveActiveProfilePromptAsync(db, workspacePath, role.Name, ct);
            var profilePromptText = ResolvePlaceholders(resolvedPrompt.Text, workflow, workspacePath, featureKey);
            var specialists = await db.SpecialistRoles.ToListAsync(ct);
            // Bug fix: role.SeedPrompt used to only be honored in StartStageAsync (the
            // interactive path) — every custom autonomous stage (anything that isn't a
            // chat-style BA clone) silently never saw its own seed instructions at all.
            var seedInstruction = role.SeedPrompt is null
                ? string.Empty
                : " " + ResolvePlaceholders(role.SeedPrompt, workflow, workspacePath, featureKey);
            var autonomyInstruction = $"You are the {role.Name} for feature '{featureKey}' in workspace '{workspacePath}'. " +
                "Work autonomously and do not ask the user for input. Produce the required artifacts.";
            var delegationClause = BuildDelegationClause(specialists, featureKey);
            var profileClause = AsAugmentingClause(profilePromptText);
            var pointsContext = BuildNegotiationContext(feature, role.Name);
            var closingInstruction = "When you are done, say DONE and provide a summary of what you changed.";
            var prompt = resolvedPrompt.OverridesBuiltIn
                ? profilePromptText
                : autonomyInstruction + seedInstruction + HandoffAutomationClause + AutonomousTersenessClause +
                  delegationClause + profileClause + guidanceContext + pointsContext + artifactContext + closingInstruction;
            var composition = resolvedPrompt.OverridesBuiltIn
                ? new PromptComposition(prompt.Length, 0, 0, 0, 0, 0, 0, 0, 0)
                : new PromptComposition(
                    autonomyInstruction.Length, seedInstruction.Length, profileClause.Length,
                    HandoffAutomationClause.Length, delegationClause.Length, guidanceContext.Length,
                    pointsContext.Length, artifactContext.Length, 0);
            try
            {
                SetCheckpoint(stageRun, StageCheckpointSignal.PromptInProgress);
                await db.SaveChangesAsync(ct);
                var promptResponse = await PromptWithDelegationAsync(acpSessionId, prompt, stageRun, workspacePath, featureKey, workflow, db, agentCts.Token);
                await RecordTurnMetricAsync(
                    db, stageRun, featureKey, workspacePath, role.Name,
                    attempt > 1 ? TurnKind.Retry : TurnKind.Stage,
                    await ResolveModelIdAsync(workspacePath, agentCts.Token),
                    prompt.Length, composition.ToJson(), promptResponse, agentCts.Token);
                SetCheckpoint(stageRun, StageCheckpointSignal.PromptSucceeded);
                ClearPromptFailure(stageRun);
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Not a failure — a person cancelled this turn. Leave the run resumable and let
                // the loop below return the unchanged release instead of a raw 500.
                cancelled = true;
                MarkPromptCancelled(stageRun);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (ProviderUnavailableException ex)
            {
                // The model provider refused the request (rate limit, unavailable, unknown model).
                // Record the real reason so the user sees "the AI service is busy", not a mystery.
                providerRefused = true;
                await RecordPromptFailureAsync(db, stageRun, ex, CancellationToken.None);
                stageRun.Summary = $"{ex.PlainReason} Run the stage again, or choose a different AI model.";
            }
            catch (AcpStalledException ex)
            {
                // The agent accepted the prompt and then produced nothing at all for minutes.
                // Record it as a failure the user can act on ("stopped responding"), then unwind
                // so the request still returns normally instead of surfacing as a 500.
                stalled = true;
                await RecordPromptFailureAsync(db, stageRun, ex, CancellationToken.None);
                stageRun.Summary = "The agent stopped responding — run the stage again.";
            }
            catch (Exception ex)
            {
                // Not ct — see the matching comment in StartStageAsync's own catch.
                await RecordPromptFailureAsync(db, stageRun, ex, CancellationToken.None);
                throw;
            }
        }

        var allPassed = true;
        // Steps that don't pass end up here regardless of whether they're inside the retry
        // Loop or a plain one-shot Builtin (code_hygiene/slice_guard/render_pr run exactly
        // once, outside the Loop, and are never retried by it) — collected so that if this
        // run ends up failed, a *future* separate RunStageAsync call (a "Run Stage Again"
        // click) still starts with a prompt that says what went wrong, instead of repeating
        // the exact same failure with no memory of it.
        var finalFailedSteps = new List<StepExecutionResult>();
        foreach (var step in role.Steps)
        {
            switch (step.Kind)
            {
                case WorkflowStepKind.Agent:
                    await PromptRoleAsync();
                    break;
                case WorkflowStepKind.Builtin:
                case WorkflowStepKind.GatePrompt:
                case WorkflowStepKind.RequiresSpecialist:
                case WorkflowStepKind.RequiresArtifact:
                    var builtin = await ExecuteStepAsync(step, role, featureKey, workspacePath, workflow, stageRun, db, ct);
                    db.ReleaseGateChecks.Add(new ReleaseGateCheck
                    {
                        StageRun = stageRun,
                        Name = builtin.StepName,
                        Passed = builtin.Passed,
                        EvidenceText = builtin.Evidence,
                        ResponsibleRole = builtin.ResponsibleRole,
                        StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-builtin.DurationMs),
                        CompletedAt = DateTimeOffset.UtcNow,
                    });
                    // Flushed immediately (not just at phase boundaries) so a concurrent poll —
                    // the frontend's live step checklist — can see progress as it happens.
                    await db.SaveChangesAsync(ct);
                    if (!builtin.Passed)
                    {
                        allPassed = false;
                        finalFailedSteps.Add(builtin);
                    }
                    break;
                case WorkflowStepKind.Loop:
                    var attempts = step.LoopAttempts ?? 3;
                    var lastAttemptPassed = false;
                    for (var attempt = 1; attempt <= attempts; attempt++)
                    {
                        var attemptPassed = true;
                        var failedSteps = new List<StepExecutionResult>();
                        foreach (var innerStep in step.LoopSteps ?? [])
                        {
                            if (innerStep.Kind == WorkflowStepKind.Agent)
                            {
                                await PromptRoleAsync(attempt);
                                if (cancelled || stalled || providerRefused) break;
                                continue;
                            }

                            var inner = await ExecuteStepAsync(innerStep, role, featureKey, workspacePath, workflow, stageRun, db, ct);
                            db.ReleaseGateChecks.Add(new ReleaseGateCheck
                            {
                                StageRun = stageRun,
                                Name = inner.StepName,
                                Passed = inner.Passed,
                                EvidenceText = inner.Evidence,
                                StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-inner.DurationMs),
                                CompletedAt = DateTimeOffset.UtcNow,
                            });
                            await db.SaveChangesAsync(ct);
                            if (!inner.Passed)
                            {
                                attemptPassed = false;
                                failedSteps.Add(inner);
                            }
                        }

                        if (cancelled || stalled || providerRefused) break;
                        lastAttemptPassed = attemptPassed;
                        if (attemptPassed) break;

                        if (attempt == attempts)
                        {
                            // Nothing left to retry into within this run — carry it forward
                            // to the final consolidated note instead (see below).
                            finalFailedSteps.AddRange(failedSteps);
                        }
                        else if (failedSteps.Count > 0)
                        {
                            // Tell the next attempt exactly what failed — without this, a retry
                            // is identical to the attempt that just failed and has no way to
                            // converge.
                            db.ReleaseGuidanceNotes.Add(new ReleaseGuidanceNote
                            {
                                StageRunId = stageRun.Id,
                                StageRun = stageRun,
                                Text = BuildGateFailureFeedback(failedSteps),
                                AddedBy = "system:gate-failure",
                            });
                            await db.SaveChangesAsync(ct);
                        }
                    }

                    if (!lastAttemptPassed) allPassed = false;
                    break;
            }

            // A cancelled or stalled prompt unwinds the whole stage immediately — see PromptRoleAsync.
            if (cancelled || stalled || providerRefused) break;
        }

        if (cancelled || stalled || providerRefused)
        {
            stageRun.FinishedAt = DateTimeOffset.UtcNow;
            feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await BroadcastEventAsync(feature.ReleaseId, "stageStateChanged", new
            {
                StageName = role.Name,
                Status = stageRun.Status.ToString(),
                Phase = stageRun.Phase.ToString(),
                AllPassed = false,
                Cancelled = cancelled,
                Stalled = stalled,
                ProviderRefused = providerRefused,
            }, ct);

            await NotifyStageFinishedAsync(role, featureKey, stageRun, allPassed: false, featureId: featureId);
            return await LoadReleaseAsync(db, feature.ReleaseId, ct);
        }

        // A consolidated record of whatever failed on this run — including gates that only
        // ever run once (code_hygiene/slice_guard/render_pr sit outside the Loop above and
        // are never retried by it) — so that if the human clicks "Run Stage Again", the
        // *next*, separate RunStageAsync call's prompts still know what went wrong here
        // instead of repeating it with no memory, same as the Loop's own inter-attempt notes.
        //
        // When a failed gate's ResponsibleRole names a different, upstream role, retrying
        // *this* role's own loop can never converge (e.g. QA's verify_code failing because
        // developer didn't write tests — QA cannot author tests). Route the feature back to
        // whoever can actually fix it instead of leaving this stage in a dead-end retry loop.
        var routed = false;
        if (!allPassed && finalFailedSteps.Count > 0)
        {
            var owner = finalFailedSteps
                .Select(s => s.ResponsibleRole)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r) && r != role.Name);

            routed = owner is not null && await RouteGateFailureToOwnerAsync(
                feature, workflow, position, role.Name, currentIndex, owner, finalFailedSteps, db, ct);

            if (!routed)
            {
                db.ReleaseGuidanceNotes.Add(new ReleaseGuidanceNote
                {
                    StageRunId = stageRun.Id,
                    StageRun = stageRun,
                    Text = BuildGateFailureFeedback(finalFailedSteps),
                    AddedBy = "system:gate-failure",
                });
                await db.SaveChangesAsync(ct);
            }
        }

        // Only worth an antagonist-review call when the deterministic gates above already
        // passed — reviewing work already known to be incomplete just burns a slow,
        // provider-dependent LLM call for no benefit.
        if (allPassed)
        {
            stageRun.Phase = StagePhase.Challenge;
            await db.SaveChangesAsync(ct);

            var challenge = workflow.Challenges.FirstOrDefault(c => c.Producer == role.Name);
            if (challenge is not null)
            {
                var challengeResult = await RunChallengeAsync(challenge, stageRun, featureKey, workspacePath, db, ct);
                if (!challengeResult.Passed) allPassed = false;
            }
        }

        // Close this stage's negotiation points when it passes, or record how it answered them
        // when it fails (so the next push-back can be more specific about what's still open).
        // Also track repeated identical failures: past the cap we stop auto-retrying and ask a
        // person instead of letting "Run Stage Again" bounce the same dead-end forever.
        if (allPassed)
        {
            await ResolveOpenPointsForStageAsync(db, feature, role.Name, ct);
            GateFailureLoopGuard.Clear(stageRun);
        }
        else
        {
            await CaptureNegotiationResponsesAsync(db, feature, role.Name, ct);
            if (!routed)
                GateFailureLoopGuard.RecordFailure(stageRun, finalFailedSteps.Select(step => step.StepName).ToList());
        }

        stageRun.ReadyToProceed = allPassed;

        if (allPassed)
            await CommitStageWorkAsync(workspacePath, role.Name, featureKey, ct);

        var isLastStage = false;
        if (allPassed)
        {
            var needsSignoff = feature.Signoffs.Any(s => s.StageName == role.Name && !s.Approved);
            stageRun.Status = needsSignoff ? ReleaseStageStatus.BlockedSignoff : ReleaseStageStatus.Complete;
            stageRun.Phase = StagePhase.Signoff;

            if (!needsSignoff)
            {
                position.CurrentStageIndex++;
                position.CurrentStageName = currentIndex + 1 < workflow.Pipeline.Count
                    ? workflow.Pipeline[currentIndex + 1].Name
                    : "done";
                position.UpdatedAt = DateTimeOffset.UtcNow;

                isLastStage = position.CurrentStageIndex >= workflow.Pipeline.Count;
                feature.Release.Status = isLastStage ? ReleaseStatus.Ready : feature.Release.Status;
            }
            else
            {
                feature.Release.Status = ReleaseStatus.Blocked;
            }
        }
        else
        {
            stageRun.Status = ReleaseStageStatus.BlockedGate;
        }

        stageRun.FinishedAt = DateTimeOffset.UtcNow;
        stageRun.Summary = allPassed
            ? $"Stage {role.Name} passed all gates and challenge."
            : stageRun.AutoRetrySuppressed
                ? "The same check keeps failing and the agent can't fix it — a person needs to look before running it again."
                : $"Stage {role.Name} failed gates or challenge.";

        // One line per stage outcome: the breadcrumb that makes a support report readable without
        // the specialist having to reconstruct the flow from the request log.
        _logger.LogInformation(
            "Stage {Stage} (feature {FeatureKey}, attempt {Attempt}) finished: status={Status} phase={Phase} allPassed={AllPassed}",
            role.Name, featureKey, stageRun.Attempt, stageRun.Status, stageRun.Phase, allPassed);

        await NotifyStageFinishedAsync(role, featureKey, stageRun, allPassed, featureId);

        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (isLastStage)
        {
                await FinalizeFeatureCompletionAndIndexAsync(db, feature, ct);
        }

        await BroadcastEventAsync(feature.ReleaseId, "stageStateChanged", new
        {
            StageName = role.Name,
            Status = stageRun.Status.ToString(),
            Phase = stageRun.Phase.ToString(),
            AllPassed = allPassed,
        }, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    public async Task<DevTeamRelease> PushBackAsync(
        Guid featureId, string targetStageName, string? instructions, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var currentIndex = position.CurrentStageIndex;
        var targetIndex = FindRoleIndex(workflow, targetStageName);

        if (targetIndex < 0)
            throw new InvalidOperationException($"No stage '{targetStageName}' in the pipeline.");
        if (targetIndex >= currentIndex)
            throw new InvalidOperationException("Push back is only allowed to a previous stage.");

        await MoveToStageAsync(
            feature, position, workflow.Pipeline[currentIndex].Name, targetIndex, targetStageName,
            instructions, "user", db, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    private static int FindRoleIndex(WorkflowDefinition workflow, string roleName)
    {
        for (var i = 0; i < workflow.Pipeline.Count; i++)
        {
            if (workflow.Pipeline[i].Name == roleName) return i;
        }
        return -1;
    }

    /// <summary>
    /// Moves a feature's FlowPosition to a previous stage, recording why (as a guidance note
    /// on the stage being left) and resetting the target stage's signoff if it was already
    /// approved. Shared core for both a human push-back (PushBackAsync, addedBy "user") and an
    /// automatic gate-ownership route (RouteGateFailureToOwnerAsync, addedBy
    /// "system:gate-failure") — the two differ only in who triggered the move and why.
    /// </summary>
    private async Task MoveToStageAsync(
        ReleaseFeature feature, ReleaseFlowPosition position, string fromStageName, int targetIndex,
        string targetStageName, string? instructions, string addedBy, DevTeamDbContext db, CancellationToken ct,
        IReadOnlyList<StepExecutionResult>? failedSteps = null)
    {
        var notesTarget = feature.StageRuns
            .Where(sr => sr.StageName == fromStageName)
            .OrderByDescending(sr => sr.StartedAt)
            .FirstOrDefault()
            ?? feature.StageRuns.OrderByDescending(sr => sr.StartedAt).FirstOrDefault();

        if (notesTarget is not null && !string.IsNullOrWhiteSpace(instructions))
        {
            db.ReleaseGuidanceNotes.Add(new ReleaseGuidanceNote
            {
                StageRunId = notesTarget.Id,
                StageRun = notesTarget,
                Text = instructions,
                AddedBy = addedBy,
            });
        }

        // Alongside the free-text note, open structured "points" so the receiving stage can be
        // handed its own numbered asks and answer them one by one (see NegotiationProtocol).
        if (notesTarget is not null)
        {
            var round = NegotiationProtocol.NextRound(PriorPointsTo(feature, targetStageName));
            OpenNegotiationPoints(db, notesTarget, targetStageName, addedBy, round, failedSteps, instructions);
        }

        position.CurrentStageIndex = targetIndex;
        position.CurrentStageName = targetStageName;
        position.UpdatedAt = DateTimeOffset.UtcNow;

        var signoff = feature.Signoffs.FirstOrDefault(s => s.StageName == targetStageName);
        if (signoff is not null && signoff.Approved)
        {
            signoff.Approved = false;
            signoff.ApprovedBy = null;
            signoff.ApprovedAt = null;
        }

        feature.Release.Status = ReleaseStatus.InProgress;
        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastEventAsync(feature.ReleaseId, "pushedBack", new
        {
            TargetStage = targetStageName,
            Instructions = instructions,
        }, ct);
    }

    /// <summary>
    /// A gate failure whose ResponsibleRole differs from the role currently running can't be
    /// fixed by retrying that role's own loop (e.g. QA's verify_code failing because developer
    /// didn't write tests — QA cannot author tests). Routes the feature back to the actual
    /// owner instead, the same way a human PushBackAsync would, but system-triggered.
    /// Returns false (falls back to the caller writing a same-stage note instead) when
    /// ResponsibleRole doesn't name a real, upstream role.
    /// </summary>
    private async Task<bool> RouteGateFailureToOwnerAsync(
        ReleaseFeature feature, WorkflowDefinition workflow, ReleaseFlowPosition position,
        string fromStageName, int fromIndex, string responsibleRole,
        IReadOnlyList<StepExecutionResult> failedSteps, DevTeamDbContext db, CancellationToken ct)
    {
        var targetIndex = FindRoleIndex(workflow, responsibleRole);
        if (targetIndex < 0 || targetIndex >= fromIndex) return false;

        // Bound the exchange: after MaxRounds of the same owner failing the same checks, stop
        // bouncing and hand it to the analyst to re-scope instead of looping forever.
        var nextRound = NegotiationProtocol.NextRound(PriorPointsTo(feature, responsibleRole));
        if (nextRound > NegotiationProtocol.MaxRounds)
        {
            var analyst = workflow.Pipeline.Count > 0 ? workflow.Pipeline[0].Name : null;
            var analystIndex = analyst is null ? -1 : FindRoleIndex(workflow, analyst);
            if (analystIndex < 0 || analystIndex >= fromIndex)
                return false;

            await MoveToStageAsync(
                feature, position, fromStageName, analystIndex, analyst,
                NegotiationProtocol.ReScopeSummary(nextRound) + Environment.NewLine + Environment.NewLine +
                    BuildGateFailureFeedback(failedSteps),
                "system:negotiation-cap", db, ct, failedSteps);
            return true;
        }

        await MoveToStageAsync(
            feature, position, fromStageName, targetIndex, responsibleRole,
            BuildGateFailureFeedback(failedSteps), "system:gate-failure", db, ct, failedSteps);
        return true;
    }

    public async Task<IReadOnlyList<MessageDto>> GetStageMessagesAsync(
        Guid featureId, Guid stageRunId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Read-only and disposed on return — see the matching comment on GetReleaseAsync.
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var feature = await LoadFeatureAsync(db, featureId, ct);
        var stageRun = feature.StageRuns.FirstOrDefault(sr => sr.Id == stageRunId)
            ?? throw new KeyNotFoundException($"Stage run {stageRunId} not found for feature {featureId}.");

        if (string.IsNullOrWhiteSpace(stageRun.AcpSessionId))
            return [];

        var sessionId = Guid.Parse(stageRun.AcpSessionId);
        var session = await db.Sessions
            .Include(s => s.Messages)
            .ThenInclude(m => m.Parts)
            .SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
            return [];

        return session.Messages
            .OrderBy(m => m.CreatedAt)
            .Select(BrokerCoordinator.ToMessageDto)
            .ToArray();
    }

    public async Task<IReadOnlyList<PipelineStageDto>> GetPipelineAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);

        return workflow.Pipeline
            .Select(r =>
            {
                var steps = FlattenStepNames(r.Steps);
                return new PipelineStageDto(
                    r.Name, r.UserInputRequired, r.Signoff, r.ExpectedArtifacts,
                    steps, steps.Select(StepFriendlyText.Describe).ToArray());
            })
            .ToArray();
    }

    // A role's steps are fully known ahead of time — this exposes them as display identifiers
    // for a live checklist (ticked off as ReleaseGateChecks/agent turns complete). A Loop's
    // inner steps are flattened in once: the checklist shows each distinct step, not one entry
    // per retry attempt.
    private static IReadOnlyList<string> FlattenStepNames(IReadOnlyList<WorkflowStep> steps)
    {
        var names = new List<string>();
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case WorkflowStepKind.Builtin:
                    names.Add(step.Builtin ?? "builtin");
                    break;
                case WorkflowStepKind.Agent:
                    names.Add($"agent:{step.AgentMode}");
                    break;
                case WorkflowStepKind.Loop:
                    names.AddRange(FlattenStepNames(step.LoopSteps ?? []));
                    break;
                case WorkflowStepKind.GatePrompt:
                    names.Add($"gate_prompt:{Truncate(step.GatePromptText ?? string.Empty, 20)}");
                    break;
                case WorkflowStepKind.RequiresSpecialist:
                    names.Add($"requires_specialist:{step.RequiredSpecialist}");
                    break;
                case WorkflowStepKind.RequiresArtifact:
                    names.Add($"requires_artifact:{step.RequiredArtifactStage}");
                    break;
            }
        }
        return names;
    }

    // Eager, crash-safe checkpoint: persists the stage run's signal/phase to CheckpointJson so
    // the startup reconciler (WorkflowCrashRecoverer) can tell exactly where a run died. Written
    // both around the agent turn (Idle/PromptInProgress/PromptSucceeded) and after the gate-step
    // ledger flushes (Gates), so recovery only ever re-issues the parts it can actually redo.
    private static void SetCheckpoint(ReleaseStageRun stageRun, StageCheckpointSignal signal,
        IReadOnlyList<CheckpointStepResult>? steps = null)
    {
        stageRun.CheckpointJson = new StageCheckpoint(
            signal, stageRun.Phase, DateTimeOffset.UtcNow, steps).Serialize();
    }

    // The gate-check names the stage's LEADING builtins (e.g. scaffold_specs/context_bundle) have
    // already recorded on stageRun.GateChecks — the same boundary RunLeadingBuiltinsAsync uses
    // (leading steps up to and including the first Agent/Loop step). Re-entrancy keeps these rows
    // and never sweeps or re-runs them, matching the engine's "these already ran at start-stage"
    // invariant.
    // A role "uses an agent" when any of its steps (directly or inside a retry loop) drives a
    // model turn. The 'verification' stage deliberately does not, which is what lets it run
    // deterministically with no opencode session — see RunStageAsync.
    private static bool RoleUsesAgent(WorkflowRole role)
        => role.Steps.Any(step =>
            step.Kind is WorkflowStepKind.Agent or WorkflowStepKind.Loop
            || (step.LoopSteps?.Any(inner => inner.Kind == WorkflowStepKind.Agent) ?? false));

    private static IReadOnlyList<string> CollectBaselineCheckNames(WorkflowRole role)
    {
        var names = new List<string>();
        foreach (var step in role.Steps)
        {
            if (step.Kind is WorkflowStepKind.Agent or WorkflowStepKind.Loop)
                break;
            if (step.Kind == WorkflowStepKind.Builtin && step.Builtin is not null)
                names.Add(step.Builtin);
        }
        return names;
    }

    private const int StageArtifactMaxChars = 20 * 1024;

    public async Task<IReadOnlyList<StageArtifactDto>> GetStageArtifactsAsync(Guid featureId, Guid stageRunId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var stageRun = feature.StageRuns.FirstOrDefault(sr => sr.Id == stageRunId)
            ?? throw new InvalidOperationException($"No stage run '{stageRunId}' for feature '{featureId}'.");
        var role = workflow.Pipeline.FirstOrDefault(r => r.Name == stageRun.StageName)
            ?? throw new InvalidOperationException($"Unknown role '{stageRun.StageName}'.");

        var workspacePath = feature.Release.WorkspacePath;
        var featureKey = feature.Key;

        var results = new List<StageArtifactDto>();
        foreach (var pattern in role.ExpectedArtifacts)
        {
            var relativePath = ResolvePlaceholders(pattern, workflow, workspacePath, featureKey);
            var fullPath = Path.Combine(workspacePath, relativePath);

            if (File.Exists(fullPath))
            {
                var content = File.ReadAllText(fullPath);
                if (content.Length > StageArtifactMaxChars)
                    content = content[..StageArtifactMaxChars] + "…(truncated)";
                results.Add(new StageArtifactDto(relativePath, content));
            }
            else
            {
                // Directory-shaped artifacts (and anything not yet produced) are reported by
                // path only — this is a skim/confirm card, not a document viewer.
                results.Add(new StageArtifactDto(relativePath, null));
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<string>> GetWorkspaceChangesAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Read-only and disposed on return — see the matching comment on GetReleaseAsync.
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var feature = await LoadFeatureAsync(db, featureId, ct);

        var status = await _gitService.StatusAsync(feature.Release.WorkspacePath, ct);
        return status.ChangedFiles ?? [];
    }

    // ─── legacy advance (runs all steps synchronously) ─────────────────────

    public async Task<DevTeamRelease> AdvanceAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var currentIndex = position.CurrentStageIndex;
        if (currentIndex >= workflow.Pipeline.Count)
        {
            feature.Release.Status = ReleaseStatus.Ready;
            feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return await LoadReleaseAsync(db, feature.ReleaseId, ct);
        }

        var role = workflow.Pipeline[currentIndex];
        var featureKey = feature.Key;

        // Check user input requirement for legacy advance
        if (role.UserInputRequired)
        {
            throw new InvalidOperationException(
                $"Stage '{role.Name}' requires user input. " +
                "Use StartStageAsync → SendMessageAsync → RunGatesAsync for interactive stages.");
        }

        // Check expected artifacts (warn but don't block)
        var missingArtifacts = new List<string>();
        foreach (var artifactPattern in role.ExpectedArtifacts)
        {
            var artifactPath = ResolvePlaceholders(artifactPattern, workflow, feature.Release.WorkspacePath, featureKey);
            var fullPath = Path.Combine(feature.Release.WorkspacePath, artifactPath);
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                missingArtifacts.Add(artifactPattern);
            }
        }

        if (missingArtifacts.Count > 0)
        {
            _logger.LogWarning("Stage '{Stage}' missing artifacts: {Artifacts}",
                role.Name, string.Join(", ", missingArtifacts));
        }

        var stageRun = new ReleaseStageRun
        {
            Feature = feature,
            StageName = role.Name,
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.Gates,
        };
        db.ReleaseStageRuns.Add(stageRun);
        await db.SaveChangesAsync(ct);

        var allPassed = true;
        foreach (var step in role.Steps)
        {
            if (step.Kind == WorkflowStepKind.Agent) continue;
            var result = await ExecuteStepAsync(step, role, featureKey, feature.Release.WorkspacePath, workflow, stageRun, db, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
                StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-result.DurationMs),
                CompletedAt = DateTimeOffset.UtcNow,
            });
            if (!result.Passed) allPassed = false;
        }

        stageRun.Status = allPassed ? ReleaseStageStatus.Complete : ReleaseStageStatus.BlockedGate;
        stageRun.Phase = StagePhase.Signoff;
        stageRun.FinishedAt = DateTimeOffset.UtcNow;
        stageRun.Summary = allPassed
            ? $"Stage {role.Name} passed all gates."
            : $"Stage {role.Name} failed gates.";

        var isLastStage = false;
        if (allPassed)
        {
            var needsSignoff = feature.Signoffs.Any(s => s.StageName == role.Name && !s.Approved);
            if (needsSignoff)
            {
                stageRun.Status = ReleaseStageStatus.BlockedSignoff;
                stageRun.Phase = StagePhase.Signoff;
                feature.Release.Status = ReleaseStatus.Blocked;
            }
            else
            {
                position.CurrentStageIndex++;
                position.CurrentStageName = currentIndex + 1 < workflow.Pipeline.Count
                    ? workflow.Pipeline[currentIndex + 1].Name
                    : "done";
                position.UpdatedAt = DateTimeOffset.UtcNow;

                isLastStage = position.CurrentStageIndex >= workflow.Pipeline.Count;
                feature.Release.Status = isLastStage ? ReleaseStatus.Ready : ReleaseStatus.InProgress;
            }
        }

        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (isLastStage)
        {
                await FinalizeFeatureCompletionAndIndexAsync(db, feature, ct);
        }

        await BroadcastEventAsync(feature.ReleaseId, "stageStateChanged", new
        {
            StageName = role.Name,
            Status = stageRun.Status.ToString(),
            Phase = stageRun.Phase.ToString(),
            AllPassed = allPassed,
        }, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    public async Task<DevTeamRelease> SignoffAsync(Guid featureId, string stageName, string role, string? comment, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var signoff = feature.Signoffs.FirstOrDefault(s => s.StageName == stageName)
            ?? throw new InvalidOperationException($"No signoff required for stage '{stageName}'.");

        signoff.Approved = true;
        signoff.ApprovedBy = role;
        signoff.Comment = comment;
        signoff.ApprovedAt = DateTimeOffset.UtcNow;

        var allSignoffsComplete = feature.Signoffs
            .Where(s => s.StageName == stageName)
            .All(s => s.Approved);

        if (allSignoffsComplete)
        {
            feature.Release.Status = ReleaseStatus.InProgress;
            feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        // When the current stage's signoff is approved, move the flow forward so
        // the feature can continue to the next stage (e.g. after autonomous run).
        var isLastStage = false;
        if (allSignoffsComplete)
        {
            var workflow = LoadWorkflow(feature.Release.WorkspacePath);
            var position = feature.FlowPosition;
            if (position is not null && position.CurrentStageName == stageName)
            {
                var workflowIndex = -1;
                for (var i = 0; i < workflow.Pipeline.Count; i++)
                {
                    if (workflow.Pipeline[i].Name == stageName)
                    {
                        workflowIndex = i;
                        break;
                    }
                }

                if (workflowIndex >= 0 && workflowIndex < workflow.Pipeline.Count)
                {
                    position.CurrentStageIndex = workflowIndex + 1;
                    isLastStage = workflowIndex + 1 >= workflow.Pipeline.Count;
                    position.CurrentStageName = isLastStage
                        ? "done"
                        : workflow.Pipeline[workflowIndex + 1].Name;
                    position.UpdatedAt = DateTimeOffset.UtcNow;

                    feature.Release.Status = isLastStage ? ReleaseStatus.Ready : ReleaseStatus.InProgress;
                    feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
        }

        if (isLastStage)
        {
            // Throws on a genuine merge conflict — the signoff itself still stands
            // (a human did approve this stage), but the feature is not marked
            // complete and the release keeps its CurrentFeatureId until resolved.
                await FinalizeFeatureCompletionAndIndexAsync(db, feature, ct);
        }

        await BroadcastEventAsync(feature.ReleaseId, "signoffApproved", new
        {
            StageName = stageName,
            Role = role,
            AllComplete = allSignoffsComplete,
        }, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    // Last-ditch escape hatch (Part: crash resilience): a user can force a fresh attempt from
    // any stuck state (BlockedGate/BlockedSignoff/BlockedEntry/Escalated, a crashed mid-gates
    // run the reconciler already healed, or even a run still wedged GatesRunning) instead of
    // being permanently dead-ended. The old run row is superseded (marked Stale, never deleted
    // — its gate ledger and findings stay visible for diagnostics) and a brand-new Active run is
    // opened for the target stage, exactly like StartStageAsync would, but without touching the
    // ACP session or dispatching a prompt: the human resumes the stage from a clean slate.
    //
    // targetStageName == null means "the current/live stage"; if the pipeline has already run to
    // completion (position past the last stage) null means the LAST stage, so retrying re-opens
    // the final role rather than throwing on an out-of-range position. An explicit name retries
    // that stage and rewinds the flow position (and the stage's signoff, if already approved)
    // to it — same rewind semantics as PushBackAsync, but allowed to target ANY stage.
    public async Task<DevTeamRelease> RetryStageAsync(Guid featureId, string? targetStageName, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var targetIndex = FindRoleIndex(workflow, targetStageName);
        if (targetIndex < 0)
        {
            if (targetStageName is null)
            {
                // "Current" stage — a pipeline that ran to completion is at the last stage by
                // definition, so retrying re-opens the final role.
                targetIndex = position.CurrentStageIndex >= workflow.Pipeline.Count
                    ? workflow.Pipeline.Count - 1
                    : position.CurrentStageIndex;
            }
            else
            {
                throw new InvalidOperationException($"No stage named '{targetStageName}' exists in the pipeline.");
            }
        }

        var targetRole = workflow.Pipeline[targetIndex];

        // Supersede every prior run for the target stage — they keep their gate ledger and
        // findings (never deleted) but are no longer the live attempt.
        var superseded = feature.StageRuns.Where(sr => sr.StageName == targetRole.Name).ToList();
        foreach (var prior in superseded)
        {
            prior.Status = ReleaseStageStatus.Stale;
            prior.ReadyToProceed = false;
            prior.FinishedAt = DateTimeOffset.UtcNow;
            prior.Summary = "Superseded by a fresh attempt.";
        }

        var nextAttempt = superseded.Count > 0
            ? superseded.Max(sr => sr.Attempt) + 1
            : 1;

        var freshRun = new ReleaseStageRun
        {
            Feature = feature,
            StageName = targetRole.Name,
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.GuidedQA,
            Attempt = nextAttempt,
            ReadyToProceed = false,
        };
        // A DbSet-level Add must be used, not feature.StageRuns.Add — a freshly created
        // run discovered through a navigation has a non-default Guid key, so the change
        // tracker treats it as an already-persisted row (Modified) and tries to UPDATE it,
        // which affects 0 rows and trips the concurrency guard.
        db.ReleaseStageRuns.Add(freshRun);

        // Rewind or pin the flow position onto the retried stage.
        position.CurrentStageIndex = targetIndex;
        position.CurrentStageName = targetRole.Name;
        position.UpdatedAt = DateTimeOffset.UtcNow;

        // Re-retrying a stage whose signoff was already approved must not keep that approval —
        // the fresh pass earns it again.
        var signoff = feature.Signoffs.FirstOrDefault(s => s.StageName == targetRole.Name);
        if (signoff is not null && signoff.Approved)
        {
            signoff.Approved = false;
            signoff.ApprovedBy = null;
            signoff.ApprovedAt = null;
            signoff.Comment = null;
        }

        feature.Release.Status = ReleaseStatus.InProgress;
        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastEventAsync(feature.ReleaseId, "stageRetried", new
        {
            StageName = targetRole.Name,
            Attempt = nextAttempt,
        }, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    // Escape hatch for a feature whose pipeline genuinely finished (FlowPosition at "done") but
    // whose ReleaseFeatureStatus never reached Complete — e.g. FinalizeFeatureCompletionAndIndexAsync
    // merged and deleted the feature branch but then failed before the Complete status was saved.
    // StartStageAsync/RunStageAsync both refuse to do anything once the position is past the last
    // stage ("All stages already complete."), so without this there is no way to resume: calling it
    // again just re-runs FinalizeFeatureCompletionAndIndexAsync, which is itself safely retryable
    // (see FinalizeFeatureCompletionAsync's branch-already-gone check).
    public async Task<DevTeamRelease> RetryFeatureFinalizationAsync(Guid featureId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow(feature.Release.WorkspacePath);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        if (position.CurrentStageIndex < workflow.Pipeline.Count)
            throw new InvalidOperationException("Feature has not finished its pipeline yet.");

        if (feature.Status != ReleaseFeatureStatus.Complete)
            await FinalizeFeatureCompletionAndIndexAsync(db, feature, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    public async Task<DevTeamRelease> GetReleaseAsync(Guid releaseId, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            // This context is used for nothing but this one read and is disposed the instant we
            // return — hot-polled every 2-3s by the frontend, so skipping EF's change-tracking
            // overhead for the whole Release->Features->StageRuns->GateChecks graph is free.
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            return await LoadReleaseAsync(db, releaseId, ct);
        }
        catch (Exception ex) when (ex is not KeyNotFoundException)
        {
            // KeyNotFoundException is the expected "no such release" path (ApiEndpoints maps
            // it to a 404) — anything else here is the kind of unexplained 500 that's hard to
            // diagnose after the fact without the release id attached to the log line.
            _logger.LogError(ex, "GetReleaseAsync failed for release {ReleaseId}", releaseId);
            throw;
        }
    }

    // Persisted "Continue automatically" state — see DevTeamRelease.AutonomousEnabled. Tracked
    // (not NoTracking, unlike GetReleaseAsync) since this call exists specifically to write.
    public async Task<DevTeamRelease> SetReleaseAutonomousEnabledAsync(Guid releaseId, bool enabled, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await db.Releases.SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        release.AutonomousEnabled = enabled;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return await LoadReleaseAsync(db, releaseId, ct);
    }

    public async Task<IReadOnlyList<DevTeamRelease>> ListReleasesAsync(string? workspacePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var releases = await db.Releases
            .Where(r => !r.IsHotfix)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns)
            .Include(r => r.Features).ThenInclude(f => f.Signoffs)
            .Include(r => r.Features).ThenInclude(f => f.FlowPosition)
            .ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(workspacePath))
            releases = releases.Where(r => IsSameWorkspace(r.WorkspacePath, workspacePath)).ToList();

        return releases.OrderByDescending(r => r.CreatedAt).ToList();
    }

    // Two path strings can refer to the same workspace while differing in slash
    // style or trailing separator (e.g. "C:\proj" vs "C:/proj/") — normalize
    // before comparing, the same way PathContainment does for containment checks.
    private static bool IsSameWorkspace(string a, string b)
    {
        try
        {
            var normalizedA = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedB = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedA.Equals(normalizedB, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<IReadOnlyList<ModelOption>> GetAvailableModelsAsync(Guid releaseId, CancellationToken ct)
    {
        var release = await GetReleaseAsync(releaseId, ct);
        return await _modelCatalog.GetAvailableModelsAsync(release.WorkspacePath, ct);
    }

    // ─── GitFlow: merge a completed feature into its release ───────────────

    // The completion hook. Calls the untouched FinalizeFeatureCompletionAsync (which does the
    // merge) and, only if that returned, queues a code-overview refresh. Indexing is best-effort:
    // a failure to queue it must never surface as a completion failure (spec REQ-001/REQ-007).
    private async Task FinalizeFeatureCompletionAndIndexAsync(DevTeamDbContext db, ReleaseFeature feature, CancellationToken ct)
    {
        await FinalizeFeatureCompletionAsync(db, feature, ct);

        try
        {
            _repoContext?.EnqueueRefresh(
                feature.Release.WorkspacePath,
                feature.Release.IsHotfix ? "hotfix-complete" : "feature-complete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Queueing the code overview refresh failed for {Workspace}", feature.Release.WorkspacePath);
        }
    }

    private async Task FinalizeFeatureCompletionAsync(DevTeamDbContext db, ReleaseFeature feature, CancellationToken ct)
    {
        var release = feature.Release;

        var checkoutResult = await _gitService.CheckoutAsync(release.WorkspacePath, release.BranchName, ct);
        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Could not check out release branch '{release.BranchName}': {checkoutResult.Message}");

        // A prior call here can merge and delete the feature branch and then fail (or crash)
        // before feature.Status = Complete is ever saved — retrying must not re-attempt a merge
        // of a branch that's already gone (MergeAsync would just fail outright), so treat a
        // missing branch as "already merged by an earlier attempt" and skip straight past it.
        var branchStillExists = (await _gitService.HeadCommitAsync(release.WorkspacePath, feature.BranchName, ct)).Success;
        if (branchStillExists)
        {
            var mergeResult = await _gitService.MergeAsync(release.WorkspacePath, feature.BranchName, release.BranchName, ct);
            if (!mergeResult.Success)
            {
                var conflictedFiles = mergeResult.ConflictedFiles ?? [];
                var prefixes = ResolveAllowedWritePrefixes(writesCode: true, "conflict-resolver", release.WorkspacePath, feature.Key, LoadWorkflow(release.WorkspacePath));
                var resolved = conflictedFiles.Length > 0 &&
                    await ResolveConflictAsync(feature.Id, release.WorkspacePath, prefixes, conflictedFiles, db, ct);
                if (!resolved)
                    throw new InvalidOperationException(
                        $"Could not merge '{feature.BranchName}' into '{release.BranchName}': {mergeResult.Message}");

                var commitResult = await _gitService.CommitAsync(
                    release.WorkspacePath, $"Merge '{feature.BranchName}' into {release.BranchName} (conflicts resolved)", ct);
                if (!commitResult.Success)
                    throw new InvalidOperationException($"Could not finalize the merge commit after resolving conflicts: {commitResult.Message}");
            }
        }
        else
        {
            _logger.LogInformation(
                "Feature branch '{Branch}' no longer exists for {FeatureKey} — an earlier finalize attempt already merged it; resuming from there.",
                feature.BranchName, feature.Key);
        }

        var authToken = await TryGetCredentialForWorkspaceAsync(db, release.WorkspacePath, ct);

        var remoteCheck = await _gitService.HasRemoteAsync(release.WorkspacePath, ct);
        if (remoteCheck.HasRemote)
        {
            var pushResult = await _gitService.PushAsync(release.WorkspacePath, release.BranchName, authToken, ct);
            if (!pushResult.Success)
                _logger.LogWarning("Push of release branch '{Branch}' failed: {Message}", release.BranchName, pushResult.Message);
        }
        else
        {
            _logger.LogInformation("No remote configured for '{Workspace}'; skipping push of release branch.", release.WorkspacePath);
        }

        if (branchStillExists)
        {
            var deleteResult = await _gitService.DeleteBranchAsync(release.WorkspacePath, feature.BranchName, authToken, ct);
            if (!deleteResult.Success)
                _logger.LogWarning("Deleting feature branch '{Branch}' failed: {Message}", feature.BranchName, deleteResult.Message);
        }

        feature.Status = ReleaseFeatureStatus.Complete;
        // The workspace is left checked out on the release branch (not any feature) at this
        // point — nothing is "active" until another feature/hotfix is created or switched to.
        await ClearActiveCheckoutAsync(db, release.WorkspacePath, ct);
        release.Status = ReleaseStatus.Ready;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await ClearActiveCheckoutAsync(db, release.WorkspacePath, ct);
    }

    private async Task<string?> TryGetCredentialForWorkspaceAsync(DevTeamDbContext db, string workspacePath, CancellationToken ct)
    {
        var settings = await db.WorkspaceGitSettings.FindAsync([workspacePath], ct);
        if (settings?.CredentialName is null) return null;
        return _credentialStore.TryGetToken(settings.CredentialName);
    }

    // ─── GitFlow: release finalization — main and develop finally get a real purpose ───

    /// <summary>
    /// An explicit user action (Part 7E — a "Ship this release" button), enabled only once a
    /// release is Ready and every one of its features has actually completed: merges the
    /// release branch into both main and develop (develop was created at git init and, until
    /// now, never merged back into by anything), pushes both, deletes the release branch, and
    /// finally sets ReleaseStatus.Released — the first place this enum value is ever set.
    /// </summary>
    public async Task<DevTeamRelease> FinalizeReleaseAsync(Guid releaseId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await db.Releases.Include(r => r.Features)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        // A hotfix's release-shell has BranchName "main" — merging "main" into "main" is a
        // harmless no-op, but the branch-delete step below would then try to delete main
        // itself. Use FinalizeHotfixAsync for a hotfix instead.
        if (release.IsHotfix)
            throw new InvalidOperationException($"{releaseId} is a hotfix — use FinalizeHotfixAsync instead.");

        if (release.Status != ReleaseStatus.Ready)
            throw new InvalidOperationException(
                $"Release must be Ready to finalize (current status: {release.Status}).");

        var incomplete = release.Features.Where(f => f.Status != ReleaseFeatureStatus.Complete).ToList();
        if (incomplete.Count > 0)
            throw new InvalidOperationException(
                "All features must be Complete before finalizing the release. Still open: " +
                string.Join(", ", incomplete.Select(f => f.Key)) + ".");

        await MergeReleaseIntoAsync(db, release, "main", ct);
        await MergeReleaseIntoAsync(db, release, "develop", ct);

        var authToken = await TryGetCredentialForWorkspaceAsync(db, release.WorkspacePath, ct);

        var remoteCheck = await _gitService.HasRemoteAsync(release.WorkspacePath, ct);
        if (remoteCheck.HasRemote)
        {
            var pushMain = await _gitService.PushAsync(release.WorkspacePath, "main", authToken, ct);
            if (!pushMain.Success)
                _logger.LogWarning("Push of 'main' failed after finalizing release '{Release}': {Message}", release.Title, pushMain.Message);

            var pushDevelop = await _gitService.PushAsync(release.WorkspacePath, "develop", authToken, ct);
            if (!pushDevelop.Success)
                _logger.LogWarning("Push of 'develop' failed after finalizing release '{Release}': {Message}", release.Title, pushDevelop.Message);
        }
        else
        {
            _logger.LogInformation("No remote configured for '{Workspace}'; skipping push after finalizing release.", release.WorkspacePath);
        }

        var deleteResult = await _gitService.DeleteBranchAsync(release.WorkspacePath, release.BranchName, authToken, ct);
        if (!deleteResult.Success)
            _logger.LogWarning("Deleting release branch '{Branch}' failed: {Message}", release.BranchName, deleteResult.Message);

        release.Status = ReleaseStatus.Released;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return await LoadReleaseAsync(db, releaseId, ct);
    }

    private async Task MergeReleaseIntoAsync(DevTeamDbContext db, DevTeamRelease release, string targetBranch, CancellationToken ct)
    {
        var workspacePath = release.WorkspacePath;

        var checkoutResult = await _gitService.CheckoutAsync(workspacePath, targetBranch, ct);
        if (!checkoutResult.Success)
            throw new InvalidOperationException($"Could not check out '{targetBranch}': {checkoutResult.Message}");

        var mergeResult = await _gitService.MergeAsync(workspacePath, release.BranchName, targetBranch, ct);
        if (!mergeResult.Success)
        {
            var conflictedFiles = mergeResult.ConflictedFiles ?? [];
            // Unrestricted write scope (null) — a release merge can touch any of its features'
            // files, not just one feature's manifest-declared slice, unlike a single feature's
            // own merge/switch conflicts (see FinalizeFeatureCompletionAsync/SwitchFeatureAsync).
            var resolved = conflictedFiles.Length > 0 &&
                await ResolveConflictAsync(release.Id, workspacePath, allowedWritePrefixes: null, conflictedFiles, db, ct);
            if (!resolved)
                throw new InvalidOperationException(
                    $"Could not merge '{release.BranchName}' into '{targetBranch}': {mergeResult.Message}");

            var commitResult = await _gitService.CommitAsync(
                workspacePath, $"Merge '{release.BranchName}' into {targetBranch} (conflicts resolved)", ct);
            if (!commitResult.Success)
                throw new InvalidOperationException($"Could not finalize the merge commit after resolving conflicts: {commitResult.Message}");
        }
    }

    // ─── pipeline discipline: write scoping, leading builtins, artifact handoff ─

    /// <summary>
    /// Commits whatever the role's agent wrote to the workspace once its gates and challenge
    /// have passed. Without this, a stage's file writes are only working-tree changes — GitFlow's
    /// merge-on-signoff and push later in the pipeline would have no actual history to carry
    /// forward, silently merging nothing.
    /// </summary>
    private async Task CommitStageWorkAsync(string workspacePath, string roleName, string featureKey, CancellationToken ct)
    {
        var result = await _gitService.CommitAsync(workspacePath, $"{roleName}: {featureKey} stage complete", ct);
        if (!result.Success)
            _logger.LogWarning("Commit after stage '{Role}' for feature '{FeatureKey}' failed: {Message}", roleName, featureKey, result.Message);
    }

    /// <summary>
    /// Computes which workspace-relative directories a role's session may write within.
    /// WritesCode: false (docs-only roles like business-analyst, and non-code specialists) is
    /// confined to its own feature docs directory, never source code; WritesCode: true
    /// additionally gets the feature's actual code paths, read from the manifest
    /// scaffold_specs already produced. Falls back to the docs-only scope (never unrestricted)
    /// if the manifest is unexpectedly missing. Takes the primitives rather than a WorkflowRole
    /// so it serves both pipeline roles and specialist delegates (Part 3) identically.
    /// </summary>
    private IReadOnlyList<string> ResolveAllowedWritePrefixes(
        bool writesCode, string ownerName, string workspacePath, string featureKey, WorkflowDefinition workflow)
    {
        var featureDir = ArtifactPaths.FeatureDirRelative(featureKey);
        // Unconditional, independent of WritesCode — same treatment featureDir already gets —
        // so any role's agent can create its declared Artifact (see WorkflowRole.Artifact)
        // under docs-root/<own-stage-name>/ without needing code write access.
        var docsRoot = workflow.DocsRoot;
        if (!writesCode)
            return [featureDir, docsRoot];

        // The developer has write access to its own manifest.yaml for legitimate reasons (e.g.
        // authoring a free-form hierarchy) — a live read here would let it self-expand
        // codePaths/Shared and have a later retry's fresh session pick that straight back up,
        // making slice_scope grade the developer's own homework instead of enforcing a real
        // boundary. The scope is frozen the first time it's resolved and reused after that; see
        // DeveloperScopeSnapshotIO.
        if (string.Equals(ownerName, "developer", StringComparison.OrdinalIgnoreCase))
        {
            var frozen = DeveloperScopeSnapshotIO.TryRead(workspacePath, featureKey);
            if (frozen is not null)
                return BuildPrefixes(featureDir, docsRoot, frozen.CodePaths, frozen.CoreBack, frozen.CoreFront, frozen.Shared);
        }

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
        if (manifest is null)
        {
            _logger.LogWarning(
                "No slice manifest found for feature '{FeatureKey}' when scoping '{Owner}'; " +
                "restricting write access to the feature docs directory only.", featureKey, ownerName);
            return [featureDir, docsRoot];
        }

        var (coreBack, coreFront) = CorePaths.Resolve(workflow.Slices, manifest);
        if (string.Equals(ownerName, "developer", StringComparison.OrdinalIgnoreCase))
        {
            DeveloperScopeSnapshotIO.WriteIfAbsent(workspacePath, featureKey,
                new DeveloperScopeSnapshot(manifest.EffectiveCodePaths, coreBack, coreFront, manifest.Shared.ToList()));
        }

        return BuildPrefixes(featureDir, docsRoot, manifest.EffectiveCodePaths, coreBack, coreFront, manifest.Shared);

        static IReadOnlyList<string> BuildPrefixes(
            string featureDir, string docsRoot, IReadOnlyList<string> codePaths, string coreBack, string coreFront, IReadOnlyList<string> shared)
        {
            var prefixes = new List<string> { featureDir, docsRoot };
            prefixes.AddRange(codePaths);
            AddUnique(prefixes, coreBack);
            AddUnique(prefixes, coreFront);
            prefixes.AddRange(shared);
            return prefixes;
        }

        // A feature may legitimately resolve its core to the same directory as its own slice
        // (or have configured an empty core) — don't hand the permission policy duplicate
        // prefixes, but never drop a distinct core path: it is exactly what lets a feature
        // extend the shared core additively.
        static void AddUnique(List<string> prefixes, string prefix)
        {
            if (!string.IsNullOrWhiteSpace(prefix) && !prefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                prefixes.Add(prefix);
        }
    }

    /// <summary>
    /// Resolves one of the fixed ArtifactRoots keys to a workspace-relative directory. Used by
    /// both ResolveStageArtifactPath (structured RequiresArtifact checks) and
    /// ResolvePlaceholders (free-text prompt tokens).
    /// </summary>
    private static string ResolveRootDirectory(string rootKey, WorkflowDefinition workflow, string workspacePath, string featureKey)
    {
        switch (rootKey)
        {
            case ArtifactRoots.DocsRoot:
                return FileSystemLookup.FindEntry(workspacePath, workflow.DocsRoot) ?? Path.Combine(workspacePath, workflow.DocsRoot);
            case ArtifactRoots.FeatureDocsRoot:
                return ArtifactPaths.FeatureDir(workspacePath, featureKey);
            case ArtifactRoots.FeatureCodeRootBack:
            case ArtifactRoots.FeatureCodeRootFront:
                var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
                // Read through EffectiveCodePaths, not CodePathBack/CodePathFront directly — a
                // manifest built from the free-form codePaths list (or hand-authored YAML that
                // only declares that list) can leave CodePathBack/CodePathFront at their
                // zero-value empty string, which is not null, so a null-coalescing fallback on
                // those fields directly would never trigger. Pre-scaffold (no manifest yet, or
                // fewer declared paths than this slot), fall back to the declared pattern
                // instead of failing outright — same "soft degrade" treatment
                // ResolveAllowedWritePrefixes already gives a missing manifest.
                var index = rootKey == ArtifactRoots.FeatureCodeRootBack ? 0 : 1;
                var effectivePaths = manifest?.EffectiveCodePaths;
                string relative;
                if (effectivePaths is not null && effectivePaths.Count > index)
                {
                    relative = effectivePaths[index];
                }
                else
                {
                    // No manifest-level path for this slot either — fall back to the release's
                    // own free-form template list (same precedence idea), then its classic
                    // CodeBack/CodeFront pair.
                    var slicesCodePaths = workflow.Slices.EffectiveCodePaths;
                    relative = slicesCodePaths.Count > index
                        ? slicesCodePaths[index].Replace("<F>", featureKey)
                        : (rootKey == ArtifactRoots.FeatureCodeRootBack
                            ? workflow.Slices.CodeBack.Replace("<F>", featureKey)
                            : workflow.Slices.CodeFront.Replace("<F>", featureKey));
                }
                return Path.Combine(workspacePath, relative);
            case ArtifactRoots.WorkspaceRoot:
                return workspacePath;
            default:
                throw new InvalidOperationException($"Unknown artifact root '{rootKey}'.");
        }
    }

    /// <summary>
    /// Resolves the named stage's declared Artifact to its real path — always
    /// &lt;resolved root&gt;/&lt;fileName&gt; (see WorkflowArtifact). No per-stage subfolder: the
    /// filename itself is expected to already be stage-specific (the pipeline editor derives
    /// it as "&lt;stage-key&gt;.&lt;ext&gt;"), and WorkflowDefinitionLoader's
    /// ValidateArtifactUniqueness rejects two roles sharing the same (Root, FileName) pair, so
    /// a flat layout can't collide. Returns the on-disk path if it already exists (tolerant of
    /// filesystem case differences via FileSystemLookup) or the expected path otherwise, so a
    /// "not found" gate failure can still report where it looked. Returns null if the named
    /// stage has no declared artifact (callers only reach this after load-time validation has
    /// already ruled that out for RequiresArtifact steps, but GatePromptText placeholders
    /// aren't load-time validated the same way, so this stays defensive).
    /// </summary>
    private static string? ResolveStageArtifactPath(string stageName, WorkflowDefinition workflow, string workspacePath, string featureKey)
    {
        var role = workflow.Pipeline.FirstOrDefault(r => r.Name == stageName);
        if (role?.Artifact is null)
            return null;

        var rootDir = ResolveRootDirectory(role.Artifact.Root, workflow, workspacePath, featureKey);
        var found = FileSystemLookup.FindEntry(rootDir, role.Artifact.FileName);
        return found ?? Path.Combine(rootDir, role.Artifact.FileName);
    }

    /// <summary>
    /// Makes a stage's declared Artifact (Part 4) enforceable: passes only if the named
    /// stage's file exists (and, for ArtifactKind.Json, parses). Deterministic — no LLM call,
    /// unlike GatePrompt — reusing FileSystemLookup so the check is cross-platform-safe.
    /// </summary>
    private static StepExecutionResult ExecuteRequiresArtifactAsync(
        string stageName, WorkflowDefinition workflow, string workspacePath, string featureKey)
    {
        var role = workflow.Pipeline.FirstOrDefault(r => r.Name == stageName);
        var artifact = role?.Artifact;
        var stepName = $"requires_artifact:{stageName}";
        if (artifact is null)
        {
            return new StepExecutionResult
            {
                StepName = stepName,
                Passed = false,
                Reason = $"'{stageName}' has no declared artifact.",
                Evidence = $"'{stageName}' has no declared artifact.",
            };
        }

        var path = ResolveStageArtifactPath(stageName, workflow, workspacePath, featureKey);
        if (path is null || !File.Exists(path))
        {
            return new StepExecutionResult
            {
                StepName = stepName,
                Passed = false,
                Reason = $"{stageName}'s declared artifact has not been produced yet.",
                Evidence = $"Expected a file at {path ?? "(unresolved)"} — not found.",
            };
        }

        if (artifact.Kind == ArtifactKind.Json)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new StepExecutionResult
                {
                    StepName = stepName,
                    Passed = false,
                    Reason = $"{stageName}'s declared artifact is not valid JSON.",
                    Evidence = $"{path}: {ex.Message}",
                };
            }
        }

        return new StepExecutionResult
        {
            StepName = stepName,
            Passed = true,
            Evidence = $"{stageName}'s declared artifact is present at {path}.",
        };
    }

    /// <summary>
    /// Resolves every known placeholder token in free-text prompt surfaces (GatePromptText,
    /// SeedPrompt, ExpectedArtifacts patterns) — a closed-set sequential string-replace, not a
    /// template engine, so there's no open-ended parsing/injection surface. Tokens: "&lt;F&gt;"
    /// (the existing feature-key convention), each ArtifactRoots key, and one
    /// "&lt;stage-name/artifact.file&gt;" entry per role that declares an Artifact, resolving to
    /// its fully-resolved real path.
    /// </summary>
    private static string ResolvePlaceholders(string text, WorkflowDefinition workflow, string workspacePath, string featureKey)
    {
        var resolved = text.Replace("<F>", featureKey);

        foreach (var rootKey in ArtifactRoots.All)
            resolved = resolved.Replace($"<{rootKey}>", ResolveRootDirectory(rootKey, workflow, workspacePath, featureKey));

        foreach (var role in workflow.Pipeline)
        {
            if (role.Artifact is null) continue;
            var token = $"<{role.Name}/artifact.file>";
            if (resolved.Contains(token, StringComparison.Ordinal))
                resolved = resolved.Replace(token, ResolveStageArtifactPath(role.Name, workflow, workspacePath, featureKey));
        }

        return resolved;
    }

    private sealed record DelegateRequestPayload(string? Role, string? Question);

    private static readonly System.Text.Json.JsonSerializerOptions DelegateRequestJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    // Only added to a prompt when at least one specialist is registered, so a workspace that
    // never uses this feature sees no change to its prompts at all.
    private static string BuildDelegationClause(IReadOnlyList<SpecialistRole> specialists, string featureKey)
    {
        if (specialists.Count == 0) return string.Empty;

        var roster = string.Join("; ", specialists.Select(s => $"{s.Name} ({s.Description})"));
        return " If you need help outside your own expertise, you can consult a specialist: " + roster + ". " +
               "To do so, write exactly one JSON object like {\"role\": \"<name>\", \"question\": \"<your question>\"} " +
               $"to devteam/features/{featureKey}/delegate-request.json using your file tools, then end your turn — " +
               "the specialist's answer will be given to you in the next message.";
    }

    /// <summary>
    /// Prompts a session and, if the agent asked to consult a specialist (see
    /// BuildDelegationClause), runs the round trip and re-prompts with the answer before
    /// returning — up to MaxDelegationRoundTrips times. A stage that never uses delegation
    /// behaves exactly as a plain PromptWithSessionRecoveryAsync call would.
    /// </summary>
    private async Task<PromptResponse> PromptWithDelegationAsync(
        Guid acpSessionId, string prompt, ReleaseStageRun stageRun, string workspacePath, string featureKey,
        WorkflowDefinition workflow, DevTeamDbContext db, CancellationToken ct)
    {
        var response = await _coordinator.PromptWithSessionRecoveryAsync(acpSessionId, prompt, ct, isPriming: true);
        var requestPath = ArtifactPaths.DelegateRequestPath(workspacePath, featureKey);

        for (var round = 0; round < MaxDelegationRoundTrips && File.Exists(requestPath); round++)
        {
            var followUp = await HandleDelegationRequestAsync(requestPath, stageRun, workspacePath, featureKey, workflow, db, ct);
            response = await _coordinator.PromptWithSessionRecoveryAsync(acpSessionId, followUp, ct, isPriming: false);
            await RecordTurnMetricAsync(
                db, stageRun, featureKey, workspacePath, stageRun.StageName, TurnKind.Delegation,
                await ResolveModelIdAsync(workspacePath, ct), followUp.Length, null, response, ct);
        }

        // A well-behaved agent stops asking once it has what it needs; if the file is still
        // there after the cap, don't leave a stale request lying around for the next turn.
        if (File.Exists(requestPath))
        {
            try { File.Delete(requestPath); } catch (IOException) { /* best-effort cleanup */ }
        }

        return response;
    }

    private async Task<string> HandleDelegationRequestAsync(
        string requestPath, ReleaseStageRun stageRun, string workspacePath, string featureKey,
        WorkflowDefinition workflow, DevTeamDbContext db, CancellationToken ct)
    {
        string requestJson;
        try
        {
            requestJson = await File.ReadAllTextAsync(requestPath, ct);
            File.Delete(requestPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read delegate-request.json for feature {FeatureKey}", featureKey);
            return "Your delegation request could not be read. Continue your work without it.";
        }

        DelegateRequestPayload? request;
        try
        {
            request = System.Text.Json.JsonSerializer.Deserialize<DelegateRequestPayload>(requestJson, DelegateRequestJsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            request = null;
        }

        if (string.IsNullOrWhiteSpace(request?.Role) || string.IsNullOrWhiteSpace(request.Question))
        {
            return "Your delegate-request.json must look like {\"role\": \"<name>\", \"question\": \"<text>\"}. " +
                   "Continue your work without a specialist for now.";
        }

        var specialist = await db.SpecialistRoles.FirstOrDefaultAsync(s => s.Name == request.Role, ct);
        if (specialist is null)
        {
            var known = await db.SpecialistRoles.Select(s => s.Name).ToListAsync(ct);
            return known.Count == 0
                ? "There are no registered specialists to consult. Continue your work without one."
                : $"Unknown specialist '{request.Role}'. Registered specialists: {string.Join(", ", known)}. Continue your work.";
        }

        try
        {
            var allowedPrefixes = ResolveAllowedWritePrefixes(specialist.WritesCode, specialist.Name, workspacePath, featureKey, workflow);
            var session = await _coordinator.NewSessionAsync(workspacePath, await ResolveModelIdAsync(workspacePath, ct), allowedPrefixes, ct);
            await _coordinator.PromptWithSessionRecoveryAsync(
                session.SessionId, specialist.PrimingPrompt + "\n\nQuestion from another stage: " + request.Question,
                ct, isPriming: true);
            var answer = await GetLatestAssistantTextAsync(db, session.SessionId, ct) ?? "(the specialist gave no response)";

            db.SpecialistConsultations.Add(new SpecialistConsultation
            {
                StageRun = stageRun,
                SpecialistName = specialist.Name,
                Question = request.Question,
                ResponseText = answer,
            });
            await db.SaveChangesAsync(ct);

            return $"The {specialist.Name} specialist responded: {answer}\n\nContinue your work with this guidance.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Consulting specialist {Specialist} failed", specialist.Name);
            return $"Consulting {specialist.Name} failed ({ex.Message}). Continue your work without it.";
        }
    }

    /// <summary>
    /// LLM-assisted resolution for a merge/stash-apply conflict (Part 7D), modeled directly on
    /// the specialist-delegation pattern above: opens a new, separately-scoped session for the
    /// resolution sub-task rather than reusing the calling session. The conflicted files
    /// already contain git's native &lt;&lt;&lt;&lt;&lt;&lt;&lt;/=======/&gt;&gt;&gt;&gt;&gt;&gt;&gt; markers on disk, so no
    /// bespoke diff-injection prompt is needed — same as every other agent turn in this app:
    /// point at the workspace, give an instruction, let it use file tools. StageAllAsync (git
    /// add -A) after each attempt is the deterministic signal for whether git still considers
    /// anything unmerged — capped at MaxConflictResolutionAttempts; past the cap the conflict
    /// is left in place (never force-aborted) and the caller surfaces a clear failure instead
    /// of silently losing work. Every attempt is recorded in MergeConflictResolution for
    /// diagnostics, mirroring how SpecialistConsultation records a delegation round-trip.
    /// allowedWritePrefixes is left to the caller: a feature-scoped conflict (a switch's
    /// stash-apply, or a feature-to-release merge) passes the same manifest-derived scope
    /// ResolveAllowedWritePrefixes already computes for that feature, while a release-scoped
    /// conflict (7E's release-to-main/develop merge, which can touch any feature's files) needs
    /// unrestricted access — null, same convention as every other unscoped session in this app.
    /// </summary>
    private async Task<bool> ResolveConflictAsync(
        Guid subjectId, string workspacePath, IReadOnlyList<string>? allowedWritePrefixes,
        IReadOnlyList<string> conflictedFiles, DevTeamDbContext db, CancellationToken ct)
    {
        var remaining = conflictedFiles;
        for (var attempt = 1; attempt <= MaxConflictResolutionAttempts; attempt++)
        {
            var session = await _coordinator.NewSessionAsync(workspacePath, await ResolveModelIdAsync(workspacePath, ct), allowedWritePrefixes, ct);
            await _coordinator.PromptWithSessionRecoveryAsync(
                session.SessionId, BuildConflictResolutionPrompt(remaining), ct, isPriming: true);
            var answer = await GetLatestAssistantTextAsync(db, session.SessionId, ct) ?? "(no response)";

            var stageResult = await _gitService.StageAllAsync(workspacePath, ct);
            var stillConflicted = stageResult.ConflictedFiles ?? [];
            var succeeded = stillConflicted.Length == 0;

            db.MergeConflictResolutions.Add(new MergeConflictResolution
            {
                SubjectId = subjectId,
                ConflictedFiles = string.Join(", ", remaining),
                Attempt = attempt,
                Succeeded = succeeded,
                ResponseText = answer,
            });
            await db.SaveChangesAsync(ct);

            if (succeeded) return true;
            remaining = stillConflicted;
        }
        return false;
    }

    private static string BuildConflictResolutionPrompt(IReadOnlyList<string> conflictedFiles)
    {
        var files = string.Join(", ", conflictedFiles);
        return $"A git merge left unresolved conflicts in these files: {files}. Each file contains git's " +
               "standard conflict markers (<<<<<<<, =======, >>>>>>>). Open each one, decide the correct " +
               "final content given both sides, and edit the file to contain only that content with every " +
               "marker removed. Do this for every listed file, then end your turn.";
    }

    /// <summary>
    /// Runs a role's steps up to (but not including) the first Agent/Loop step — the builtins
    /// that must produce scaffolding/context before the interactive or autonomous conversation
    /// starts (e.g. scaffold_specs/context_bundle for business-analyst). Records a gate check
    /// per executed step so RunGatesAsync doesn't need to re-run them afterward.
    /// </summary>
    /// <summary>
    /// Runs a role's EntryGates — checks that must pass before its turn starts at all,
    /// symmetric to the existing exit-side Steps. On failure, marks the stage run
    /// BlockedEntry (distinct from BlockedGate, which means this stage's own exit gates
    /// failed after it ran) and stops short of ever sending the first prompt.
    /// </summary>
    private async Task<bool> RunEntryGatesAsync(
        WorkflowRole role, string featureKey, string workspacePath, WorkflowDefinition workflow,
        ReleaseStageRun stageRun, DevTeamDbContext db, CancellationToken ct)
    {
        var allPassed = true;
        foreach (var gate in role.EntryGates ?? [])
        {
            var result = await ExecuteStepAsync(gate, role, featureKey, workspacePath, workflow, stageRun, db, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
                ResponsibleRole = result.ResponsibleRole,
                IsEntryGate = true,
                StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-result.DurationMs),
                CompletedAt = DateTimeOffset.UtcNow,
            });
            if (!result.Passed) allPassed = false;
        }

        if (!allPassed)
        {
            stageRun.Status = ReleaseStageStatus.BlockedEntry;
            stageRun.FinishedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return allPassed;
    }

    private async Task<bool> RunLeadingBuiltinsAsync(
        WorkflowRole role, string featureKey, string workspacePath, WorkflowDefinition workflow,
        ReleaseStageRun stageRun, DevTeamDbContext db, CancellationToken ct)
    {
        var allPassed = true;
        foreach (var step in role.Steps)
        {
            if (step.Kind is WorkflowStepKind.Agent or WorkflowStepKind.Loop)
                break;

            var result = await ExecuteStepAsync(step, role, featureKey, workspacePath, workflow, stageRun, db, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
                ResponsibleRole = result.ResponsibleRole,
                StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-result.DurationMs),
                CompletedAt = DateTimeOffset.UtcNow,
            });
            if (!result.Passed) allPassed = false;
        }
        return allPassed;
    }

    /// <summary>
    /// Builds the verbatim artifact content to splice into a stage's opening prompt, so the
    /// next role starts from what the previous stage actually produced instead of discovering
    /// (or ignoring) it on its own initiative. Every later stage gets context.md verbatim
    /// (written by the context_bundle builtin); the first stage in the pipeline has no
    /// upstream stage to read from, so it gets its own just-scaffolded manifest + requirements
    /// skeleton instead.
    /// </summary>
    private static string BuildArtifactContext(string workspacePath, string featureKey, bool isFirstStage)
    {
        if (isFirstStage)
        {
            var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
            var brsPath = ArtifactPaths.BrsPath(workspacePath, featureKey);
            var hasRequirements = File.Exists(brsPath);
            var interviewPath = ArtifactPaths.InterviewPath(workspacePath, featureKey);
            var hasInterview = File.Exists(interviewPath);
            if (manifest is null && !hasRequirements && !hasInterview)
                return string.Empty;

            var scaffold = new StringBuilder();
            scaffold.AppendLine();
            scaffold.AppendLine();
            scaffold.AppendLine("--- Feature scaffold (from scaffold_specs) ---");
            if (manifest is not null)
            {
                scaffold.AppendLine($"Title: {manifest.Title}");
                foreach (var codePath in manifest.EffectiveCodePaths)
                    scaffold.AppendLine($"Code path: {codePath}");
            }
            if (hasRequirements)
            {
                scaffold.AppendLine();
                scaffold.Append(File.ReadAllText(brsPath));
            }
            if (hasInterview)
            {
                scaffold.AppendLine();
                scaffold.AppendLine("--- Interview so far (resume: continue from the last unanswered question; do NOT re-ask what is already answered) ---");
                scaffold.Append(File.ReadAllText(interviewPath));
            }
            return scaffold.ToString();
        }

        var contextPath = ArtifactPaths.ContextPath(workspacePath, featureKey);
        if (!File.Exists(contextPath))
            return string.Empty;

        return "\n\n--- Context from the previous stage (context.md) ---\n" + File.ReadAllText(contextPath);
    }

    // ─── internal execution ────────────────────────────────────────────────

    private async Task<StepExecutionResult> ExecuteStepAsync(
        WorkflowStep step, WorkflowRole role, string featureKey, string workspacePath, WorkflowDefinition workflow,
        ReleaseStageRun stageRun, DevTeamDbContext db, CancellationToken ct)
    {
        var result = step.Kind switch
        {
            WorkflowStepKind.Builtin => await ExecuteBuiltinAsync(step.Builtin!, featureKey, workspacePath, ct),
            WorkflowStepKind.Agent => new StepExecutionResult { StepName = $"agent:{step.AgentMode}", Passed = true, Evidence = "Interactive — handled via chat." },
            WorkflowStepKind.Loop => await ExecuteLoopAsync(step, role, featureKey, workspacePath, workflow, stageRun, db, ct),
            WorkflowStepKind.GatePrompt => await ExecuteGatePromptAsync(
                ResolvePlaceholders(step.GatePromptText!, workflow, workspacePath, featureKey),
                $"gate_prompt:{role.Name}:{Truncate(step.GatePromptText!, 40)}",
                role.Name,
                $"Gate prompt found issues with {role.Name}'s work.",
                stageRun, workspacePath, db, ct),
            WorkflowStepKind.RequiresSpecialist => await ExecuteRequiresSpecialistAsync(step.RequiredSpecialist!, stageRun, db, ct),
            WorkflowStepKind.RequiresArtifact => ExecuteRequiresArtifactAsync(step.RequiredArtifactStage!, workflow, workspacePath, featureKey),
            _ => throw new InvalidOperationException($"Unknown step kind: {step.Kind}"),
        };
        return result with { ResponsibleRole = step.ResponsibleRole ?? result.ResponsibleRole };
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

    private async Task<StepExecutionResult> ExecuteBuiltinAsync(string gateName, string featureKey, string workspacePath, CancellationToken ct)
    {
        var request = new GateRequest(gateName, workspacePath, featureKey, Inputs: await BuildBuiltinInputsAsync(gateName, featureKey, workspacePath, ct));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _gateRunner.RunAsync(gateName, request, ct);
        stopwatch.Stop();
        return new StepExecutionResult
        {
            StepName = gateName,
            Passed = result.Passed,
            Reason = result.Reason,
            Evidence = result.EvidenceText,
            DurationMs = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Makes delegation (Part 3) enforceable instead of purely advisory: passes only if a
    /// SpecialistConsultation already exists for this exact stage run and the named
    /// specialist. Deterministic — a plain DB fact, not LLM-graded like GatePrompt.
    /// </summary>
    private static async Task<StepExecutionResult> ExecuteRequiresSpecialistAsync(
        string specialistName, ReleaseStageRun stageRun, DevTeamDbContext db, CancellationToken ct)
    {
        var consulted = await db.SpecialistConsultations
            .AnyAsync(c => c.StageRunId == stageRun.Id && c.SpecialistName == specialistName, ct);

        return new StepExecutionResult
        {
            StepName = $"requires_specialist:{specialistName}",
            Passed = consulted,
            Reason = consulted ? null : $"{specialistName} has not been consulted yet for this stage run.",
            Evidence = consulted
                ? $"A consultation with {specialistName} is on record for this stage run."
                : $"Write devteam/features/<F>/delegate-request.json with role \"{specialistName}\" to consult them before this can pass.",
        };
    }

    private static async Task<IReadOnlyDictionary<string, string>> BuildBuiltinInputsAsync(string gateName, string featureKey, string workspacePath, CancellationToken ct)
    {
        if (gateName is not BuiltinRegistry.GherkinValidator
            and not BuiltinRegistry.CoverageMatrix
            and not BuiltinRegistry.ScaffoldSpecs
            and not BuiltinRegistry.RenderPr)
            return new Dictionary<string, string>();

        var requirements = RequirementsExtractor.Extract(workspacePath, featureKey);
        var inputs = new Dictionary<string, string>
        {
            ["requirementsJson"] = System.Text.Json.JsonSerializer.Serialize(requirements),
        };

        if (gateName is BuiltinRegistry.CoverageMatrix or BuiltinRegistry.RenderPr)
        {
            // Only this feature's tests — requirement ids are per-feature, so a workspace-wide scan
            // would let one feature's tests satisfy another's requirements.
            var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
            var testFiles = FeatureTestFiles.Discover(workspacePath, featureKey, manifest);
            if (testFiles.Count > 0)
                inputs["testFilesJson"] = System.Text.Json.JsonSerializer.Serialize(testFiles);

            var testCommand = manifest?.TestCommand ?? "dotnet test DevTeam.slnx";
            inputs["testOutput"] = await CaptureTestOutputAsync(testCommand, workspacePath, ct);
        }

        return inputs;
    }

    private static async Task<string> CaptureTestOutputAsync(string commandLine, string workspacePath, CancellationToken ct)
    {
        try
        {
            var trimmed = commandLine.Trim();
            var separator = trimmed.IndexOf(' ');
            var fileName = separator <= 0 ? trimmed : trimmed[..separator];
            var arguments = separator <= 0 ? string.Empty : trimmed[(separator + 1)..];
            var result = await new SystemProcessRunner().RunAsync(
                new ProcessRunRequest(fileName, arguments, workspacePath),
                ct);
            return TestOutputNormalizer.Normalize(result.StandardOutput + "\n" + result.StandardError);
        }
        catch (Exception)
        {
            return TestOutputNormalizer.Normalize(string.Empty);
        }
    }

    private async Task<StepExecutionResult> ExecuteLoopAsync(
        WorkflowStep step, WorkflowRole role, string featureKey, string workspacePath, WorkflowDefinition workflow,
        ReleaseStageRun stageRun, DevTeamDbContext db, CancellationToken ct)
    {
        var maxAttempts = step.LoopAttempts ?? 3;
        var lastResult = new StepExecutionResult { StepName = "loop", Passed = false };
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            foreach (var innerStep in step.LoopSteps ?? [])
            {
                if (innerStep.Kind == WorkflowStepKind.Agent) continue;
                lastResult = await ExecuteStepAsync(innerStep, role, featureKey, workspacePath, workflow, stageRun, db, ct);
                if (!lastResult.Passed) break;
            }
            if (lastResult.Passed) break;
        }
        return lastResult with { StepName = $"loop({maxAttempts})" };
    }

    private async Task<StepExecutionResult> ExecuteGatePromptAsync(
        string promptText, string stepName, string target, string findingSummary,
        ReleaseStageRun stageRun, string workspacePath, DevTeamDbContext db, CancellationToken ct)
    {
        try
        {
            var session = await _coordinator.NewSessionAsync(workspacePath, await ResolveModelIdAsync(workspacePath, ct), null, ct);
            var response = await _coordinator.PromptWithSessionRecoveryAsync(session.SessionId, promptText, ct, isPriming: true);
            await RecordTurnMetricAsync(
                db, stageRun, null, workspacePath, stageRun.StageName, TurnKind.GatePrompt,
                await ResolveModelIdAsync(workspacePath, ct), promptText.Length, null, response, ct);

            var passed = response.StopReason == "end_turn";
            if (!passed)
            {
                db.ReviewFindings.Add(new ReviewFinding
                {
                    StageRun = stageRun,
                    Target = target,
                    Kind = ReviewFindingKind.Requirement,
                    Severity = ReviewFindingSeverity.Major,
                    Summary = findingSummary,
                    Status = ReviewFindingStatus.Open,
                });
            }

            return new StepExecutionResult { StepName = stepName, Passed = passed, Evidence = $"Reviewed by LLM prompt: {target}." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gate prompt {StepName} failed for {Target}", stepName, target);
            return new StepExecutionResult { StepName = stepName, Passed = true, Evidence = $"Gate prompt skipped: {ex.Message}" };
        }
    }

    private Task<StepExecutionResult> RunChallengeAsync(WorkflowChallenge challenge, ReleaseStageRun stageRun, string featureKey, string workspacePath, DevTeamDbContext db, CancellationToken ct)
    {
        var antagonistMode = challenge.AntagonistMode ?? "build";
        var prompt = $"You are reviewing the {challenge.Producer}'s work for feature '{featureKey}'. " +
                     $"Review for completeness, correctness, and quality. " +
                     $"Report any issues, ambiguities, or gaps as findings.";

        return ExecuteGatePromptAsync(
            prompt,
            $"challenge:{challenge.Producer}->{antagonistMode}",
            challenge.Producer,
            $"Challenge agent ({antagonistMode}) found issues.",
            stageRun, workspacePath, db, ct);
    }

    private async Task BroadcastEventAsync(Guid releaseId, string type, object payload, CancellationToken ct)
    {
        try
        {
            var evt = new StreamEvent(releaseId.ToString(), type, System.Text.Json.JsonSerializer.SerializeToElement(payload));
            await _broadcaster.BroadcastToReleaseAsync(releaseId, evt, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to broadcast {Type} for {ReleaseId}.", type, releaseId); }
    }

    // Reads devteam/release.yaml from the workspace when the user has authored one, so a
    // custom/reordered pipeline is opt-in per workspace; absent, every workspace keeps
    // behaving exactly as the hardcoded default (zero migration risk for existing releases).
    private WorkflowDefinition LoadWorkflow(string workspacePath)
    {
        var path = ArtifactPaths.ReleaseYamlPath(workspacePath);
        return File.Exists(path) ? _loader.Load(File.ReadAllText(path)) : _loader.LoadDefault();
    }

    private static async Task<string?> GetLatestAssistantTextAsync(
        DevTeamDbContext db, Guid sessionId, CancellationToken ct)
    {
        var candidates = await db.Messages
            .Where(m => m.SessionId == sessionId && m.Role == "assistant" && m.BodyText != null)
            .ToListAsync(ct);
        return candidates
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefault()?.BodyText;
    }

    // A profile's resolved prompt for one role, plus whether it should replace the entire
    // built-in prompt (OverridesBuiltIn) or just be appended after it. Text is empty when
    // no profile/prompt is set, so this stays a no-op until the user creates one.
    private readonly record struct ResolvedProfilePrompt(string Text, bool OverridesBuiltIn);

    // Resolves the workspace's active profile (its own override, falling back to the global
    // default, falling back to nothing) and returns that profile's prompt for this role.
    // Callers decide how to fold OverridesBuiltIn into the final prompt they build — this
    // method itself makes no assumption about append-vs-replace.
    private static async Task<ResolvedProfilePrompt> ResolveActiveProfilePromptAsync(
        DevTeamDbContext db, string workspacePath, string roleName, CancellationToken ct)
    {
        var workspaceProfileId = await db.WorkspaceProfileSettings
            .Where(s => s.WorkspacePath == workspacePath)
            .Select(s => (Guid?)s.ProfileId)
            .FirstOrDefaultAsync(ct);

        var profileId = workspaceProfileId ?? await db.Profiles
            .Where(p => p.IsDefault)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null) return new ResolvedProfilePrompt(string.Empty, false);

        var prompt = await db.ProfilePrompts
            .Where(p => p.ProfileId == profileId && p.StageName == roleName)
            .Select(p => new { p.PromptText, p.OverridesBuiltInPrompt })
            .FirstOrDefaultAsync(ct);

        if (prompt is null || string.IsNullOrWhiteSpace(prompt.PromptText))
            return new ResolvedProfilePrompt(string.Empty, false);

        return new ResolvedProfilePrompt(prompt.PromptText, prompt.OverridesBuiltInPrompt);
    }

    // Wraps a profile's prompt as a clearly delimited, appended clause — used only when the
    // profile is in augment mode (OverridesBuiltIn is false). In override mode the profile's
    // text becomes the entire prompt instead, so this is never called for it.
    private static string AsAugmentingClause(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty :
        Environment.NewLine + Environment.NewLine +
        "--- Additional guidance from your active profile ---" + Environment.NewLine +
        text;

    // Turns a failed attempt's gate results into the next attempt's guidance — without this
    // a retry is a byte-for-byte repeat of the attempt that just failed, with no way to
    // converge. Each step's evidence is capped so a chatty verify_code test-output dump
    // can't blow out the next prompt's token budget.
    private const int MaxEvidenceCharsPerStep = 1500;

    private static string BuildGateFailureFeedback(IReadOnlyList<StepExecutionResult> failedSteps)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Your previous attempt failed the following checks — fix these before continuing:");
        foreach (var step in failedSteps)
        {
            builder.Append("- ").Append(step.StepName).Append(": ").AppendLine(step.Reason ?? "failed");
            var evidence = step.Evidence;
            if (string.IsNullOrWhiteSpace(evidence)) continue;
            if (evidence.Length > MaxEvidenceCharsPerStep)
                evidence = evidence[..MaxEvidenceCharsPerStep] + "…(truncated)";
            builder.AppendLine(evidence);
        }
        return builder.ToString().TrimEnd();
    }

    // A stage sitting idle with no reply looks identical whether the agent is legitimately
    // slow or something actually died — this is the only thing that makes that distinction
    // visible/queryable after the fact (BrokerCoordinator's own recovery already retries once
    // transparently; this only fires when that retry ALSO failed, i.e. a real, reportable
    // problem). Escalates disconnects/provider rejections (a blind retry won't help a
    // provider rejection, so the human needs to know); a bare timeout is reported but left
    // non-escalating since it's the most routine of the three and still auto-retryable.
    private static async Task RecordPromptFailureAsync(
        DevTeamDbContext db, ReleaseStageRun stageRun, Exception ex, CancellationToken ct)
    {
        var (kind, escalate) = ex switch
        {
            AcpDisconnectedException => (StageErrorKind.Disconnected, true),
            RpcException => (StageErrorKind.ProviderRejected, true),
            AcpStalledException => (StageErrorKind.Stalled, true),
            ProviderUnavailableException => (StageErrorKind.ProviderUnavailable, true),
            OperationCanceledException or TimeoutException => (StageErrorKind.TimedOut, true),
            _ => ((StageErrorKind?)null, false),
        };

        if (kind is null) return;

        stageRun.LastErrorKind = kind.Value;
        stageRun.LastErrorMessage = ex.Message;
        stageRun.LastErrorAt = DateTimeOffset.UtcNow;
        if (escalate) stageRun.Status = ReleaseStageStatus.Escalated;
        await db.SaveChangesAsync(ct);
    }

    // A user-cancelled turn is not a stage failure: leave the run resumable (still Active, no
    // error marker, a plain "cancelled" summary) instead of escalating it as an agent error.
    private static void MarkPromptCancelled(ReleaseStageRun stageRun)
    {
        ClearPromptFailure(stageRun);
        stageRun.Status = ReleaseStageStatus.Active;
        stageRun.Phase = StagePhase.GuidedQA;
        stageRun.ReadyToProceed = false;
        stageRun.FinishedAt = null;
        stageRun.Summary = "Cancelled — run the stage again when you're ready.";
    }

    private static void ClearPromptFailure(ReleaseStageRun stageRun)
    {
        stageRun.LastErrorKind = StageErrorKind.None;
        stageRun.LastErrorMessage = null;
        stageRun.LastErrorAt = null;
        // Escalated is a transient "last attempt failed" marker, not a durable status —
        // once a prompt succeeds again the stage is simply back to working normally.
        if (stageRun.Status == ReleaseStageStatus.Escalated)
            stageRun.Status = ReleaseStageStatus.Active;
    }

    // Appends the turn's answered question to the crash-proof interview log. Best-effort: a
    // failure to write it must never affect the turn the user just had.
    private async Task TryRecordInterviewTurnAsync(
        DevTeamDbContext db, Guid acpSessionId, string featureKey, string workspacePath, string userText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return;

        try
        {
            var assistant = await GetLatestAssistantTextAsync(db, acpSessionId, ct);
            var questions = MultiQuestionDetector.ExtractQuestions(assistant);
            if (questions.Count == 0)
                return;

            var path = ArtifactPaths.InterviewPath(workspacePath, featureKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            File.WriteAllText(path, InterviewLog.Append(existing, questions[^1], userText));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record the interview turn for feature {FeatureKey}.", featureKey);
        }
    }

    // ─── turn metrics ──────────────────────────────────────────────────────
    // Every prompt gets a row so a stage's token total can be broken down by cause (retries,
    // the antagonist review, delegation, …) and by prompt section. Best-effort: a metrics write
    // must never break the turn it is measuring.

    private async Task RecordTurnMetricAsync(
        DevTeamDbContext db, ReleaseStageRun? stageRun, string? featureKey, string workspacePath,
        string stageName, TurnKind kind, string? modelId, int promptChars, string? breakdownJson,
        PromptResponse response, CancellationToken ct)
    {
        try
        {
            var measurement = response.Measurement;
            var durationMs = measurement?.DurationMs ?? 0;
            db.TurnMetrics.Add(new TurnMetric
            {
                StageRunId = stageRun?.Id,
                StageRun = stageRun,
                SessionId = response.SessionId,
                WorkspacePath = workspacePath,
                StageName = stageName,
                FeatureKey = featureKey,
                ModelId = modelId,
                Kind = kind,
                Attempt = stageRun?.Attempt ?? 1,
                StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-durationMs),
                DurationMs = durationMs,
                TimeToFirstEventMs = measurement?.TimeToFirstEventMs,
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens,
                TotalTokens = response.TotalTokens,
                CachedReadTokens = measurement?.CachedReadTokens,
                ContextTokens = measurement?.ContextTokens,
                CostAmount = measurement?.CostAmount,
                CostCurrency = measurement?.CostCurrency,
                PromptChars = promptChars,
                PromptBreakdownJson = breakdownJson,
                TextEvents = measurement?.TextEvents ?? 0,
                ThoughtEvents = measurement?.ThoughtEvents ?? 0,
                ToolEvents = measurement?.ToolEvents ?? 0,
                StopReason = response.StopReason,
                Outcome = Enum.TryParse<TurnOutcome>(measurement?.Outcome, ignoreCase: true, out var outcome)
                    ? outcome
                    : TurnOutcome.Ok,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record turn metrics for feature {FeatureKey}.", featureKey);
        }
    }

    private static int InterviewChars(string workspacePath, string featureKey)
    {
        var path = ArtifactPaths.InterviewPath(workspacePath, featureKey);
        try
        {
            return File.Exists(path) ? (int)new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string BuildGuidanceContext(ReleaseFeature feature)
    {
        var notes = feature.StageRuns
            .SelectMany(sr => sr.GuidanceNotes)
            .Where(n => !string.IsNullOrWhiteSpace(n.Text))
            .Select(n => n.Text)
            .Distinct()
            .ToArray();
        return notes.Length == 0
            ? ""
            : Environment.NewLine + Environment.NewLine +
              "Feedback from the team to incorporate:" + Environment.NewLine +
              string.Join(Environment.NewLine, notes.Select(n => "  - " + n));
    }

    // ─── negotiation ledger ────────────────────────────────────────────────
    // Every push-back opens numbered "points" (ReviewFinding rows) for the stage it was sent to,
    // so the receiving stage answers each one instead of re-reading a growing pile of free text.
    // The round number is what bounds the exchange (NegotiationProtocol.MaxRounds).

    private static IEnumerable<ReviewFinding> PriorPointsTo(ReleaseFeature feature, string targetStageName)
        => feature.StageRuns
            .SelectMany(sr => sr.Findings)
            .Where(f => string.Equals(f.PushedBackTo, targetStageName, StringComparison.OrdinalIgnoreCase));

    private static void OpenNegotiationPoints(
        DevTeamDbContext db, ReleaseStageRun attachTo, string targetStageName, string openedBy,
        int round, IReadOnlyList<StepExecutionResult>? failedSteps, string? instructions)
    {
        if (failedSteps is { Count: > 0 })
        {
            foreach (var step in failedSteps)
            {
                db.ReviewFindings.Add(new ReviewFinding
                {
                    StageRunId = attachTo.Id,
                    StageRun = attachTo,
                    Target = step.StepName,
                    Kind = ReviewFindingKind.Process,
                    Severity = ReviewFindingSeverity.Major,
                    Summary = $"{GateFriendlyText.Describe(step.StepName, step.Evidence).Title} didn't pass: {step.Reason ?? "failed"}",
                    Expected = Truncate(step.Evidence, MaxEvidenceCharsPerStep),
                    Round = round,
                    OpenedBy = openedBy,
                    PushedBackTo = targetStageName,
                    Status = ReviewFindingStatus.Open,
                });
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(instructions))
            return;

        db.ReviewFindings.Add(new ReviewFinding
        {
            StageRunId = attachTo.Id,
            StageRun = attachTo,
            Target = targetStageName,
            Kind = ReviewFindingKind.Process,
            Severity = ReviewFindingSeverity.Major,
            Summary = Truncate(instructions, 600),
            Round = round,
            OpenedBy = openedBy,
            PushedBackTo = targetStageName,
            Status = ReviewFindingStatus.Open,
        });
    }

    // The point-form block for the stage about to run: only its own open points, numbered.
    private static string BuildNegotiationContext(ReleaseFeature feature, string stageName)
    {
        var open = PriorPointsTo(feature, stageName)
            .Where(f => f.Status == ReviewFindingStatus.Open)
            .OrderBy(f => f.Round)
            .ThenBy(f => f.CreatedAt)
            .ToList();
        return NegotiationProtocol.BuildPointsBlock(open);
    }

    // The stage passed its checks, so every point it was sent is satisfied — close them.
    private static async Task ResolveOpenPointsForStageAsync(
        DevTeamDbContext db, ReleaseFeature feature, string stageName, CancellationToken ct)
    {
        var open = PriorPointsTo(feature, stageName)
            .Where(f => f.Status == ReviewFindingStatus.Open)
            .ToList();
        if (open.Count == 0)
            return;

        foreach (var point in open)
        {
            point.Status = ReviewFindingStatus.Resolved;
            point.ResolvedAt = DateTimeOffset.UtcNow;
            point.UpdatedAt = DateTimeOffset.UtcNow;
            point.ResolutionNote = "All checks for this stage passed.";
        }
        await db.SaveChangesAsync(ct);
    }

    // The stage failed, so record how it answered each point — the next push can then be more
    // specific about the points it still disputes, and the user can see what was claimed.
    private async Task CaptureNegotiationResponsesAsync(
        DevTeamDbContext db, ReleaseFeature feature, string stageName, CancellationToken ct)
    {
        var open = PriorPointsTo(feature, stageName)
            .Where(f => f.Status == ReviewFindingStatus.Open)
            .ToList();
        if (open.Count == 0)
            return;

        var run = feature.StageRuns
            .Where(sr => sr.StageName == stageName)
            .OrderByDescending(sr => sr.StartedAt)
            .FirstOrDefault();
        if (run?.AcpSessionId is null || !Guid.TryParse(run.AcpSessionId, out var sessionId))
            return;

        var text = await GetLatestAssistantTextAsync(db, sessionId, ct);
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (var response in NegotiationProtocol.ParseResponses(text, open))
        {
            response.Point.ResponseKind = response.Kind;
            response.Point.ResponseText = Truncate(response.Detail, 600);
            response.Point.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task<ReleaseFeature> LoadFeatureAsync(DevTeamDbContext db, Guid featureId, CancellationToken ct)
        => await db.ReleaseFeatures
            .Include(f => f.Release)
            .Include(f => f.StageRuns).ThenInclude(sr => sr.GateChecks)
            .Include(f => f.StageRuns).ThenInclude(sr => sr.Findings)
            .Include(f => f.StageRuns).ThenInclude(sr => sr.GuidanceNotes)
            .Include(f => f.StageRuns).ThenInclude(sr => sr.SpecialistConsultations)
            .Include(f => f.Signoffs)
            .Include(f => f.FlowPosition)
            .SingleOrDefaultAsync(f => f.Id == featureId, ct)
            ?? throw new KeyNotFoundException($"Feature {featureId} not found.");

    private static async Task<DevTeamRelease> LoadReleaseAsync(DevTeamDbContext db, Guid releaseId, CancellationToken ct)
    {
        var release = await db.Releases
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.GateChecks)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.Findings)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.GuidanceNotes)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.SpecialistConsultations)
            .Include(r => r.Features).ThenInclude(f => f.Signoffs)
            .Include(r => r.Features).ThenInclude(f => f.FlowPosition)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        await PopulateCurrentFeatureIdAsync(db, release, ct);
        return release;
    }

    // DevTeamRelease.CurrentFeatureId is [NotMapped] — the real source of truth is
    // WorkspaceActiveCheckout, keyed by workspace (not release), since two releases can share
    // a WorkspacePath and each must only ever claim a feature that's actually its own.
    private static async Task PopulateCurrentFeatureIdAsync(DevTeamDbContext db, DevTeamRelease release, CancellationToken ct)
    {
        var checkout = await db.WorkspaceActiveCheckouts.FindAsync([release.WorkspacePath], ct);
        var activeFeatureId = checkout?.ActiveReleaseFeatureId;
        release.CurrentFeatureId = activeFeatureId is not null && release.Features.Any(f => f.Id == activeFeatureId)
            ? activeFeatureId
            : null;
    }

    // Whatever feature is about to be displaced from this workspace's checkout (by creating
    // or switching to a different one) steps down from InProgress to OnHold — InProgress means
    // "the one currently checked out," so at most one feature per workspace should ever read
    // InProgress at a time. Applies equally to a displaced hotfix (its ReleaseFeature row is
    // read via the same ActiveReleaseFeatureId slot — see SetActiveCheckoutAsync). A no-op if
    // nothing was active.
    private static async Task MarkPreviouslyActiveFeatureOnHoldAsync(DevTeamDbContext db, string workspacePath, CancellationToken ct)
    {
        var checkout = await db.WorkspaceActiveCheckouts.FindAsync([workspacePath], ct);
        if (checkout?.ActiveReleaseFeatureId is not { } previousFeatureId)
            return;

        var previousFeature = await db.ReleaseFeatures.FindAsync([previousFeatureId], ct);
        if (previousFeature is { Status: ReleaseFeatureStatus.InProgress })
        {
            previousFeature.Status = ReleaseFeatureStatus.OnHold;
            await db.SaveChangesAsync(ct);
        }
    }

    // Upserts the single "what's checked out in this workspace" fact. isHotfix just tags
    // whether releaseFeatureId's row is a hotfix (mirroring it into ActiveHotfixId too) — it's
    // the same ReleaseFeature id space either way, so SwitchFeatureAsync's outgoing-checkout
    // check (which only reads ActiveReleaseFeatureId) works unmodified for hotfixes too.
    private static async Task SetActiveCheckoutAsync(
        DevTeamDbContext db, string workspacePath, Guid? releaseFeatureId, bool isHotfix, CancellationToken ct)
    {
        var checkout = await db.WorkspaceActiveCheckouts.FindAsync([workspacePath], ct);
        if (checkout is null)
        {
            checkout = new WorkspaceActiveCheckout { WorkspacePath = workspacePath };
            db.WorkspaceActiveCheckouts.Add(checkout);
        }

        checkout.ActiveReleaseFeatureId = releaseFeatureId;
        checkout.ActiveHotfixId = isHotfix ? releaseFeatureId : null;
        await db.SaveChangesAsync(ct);
    }

    private static Task ClearActiveCheckoutAsync(DevTeamDbContext db, string workspacePath, CancellationToken ct)
        => SetActiveCheckoutAsync(db, workspacePath, releaseFeatureId: null, isHotfix: false, ct);
}
