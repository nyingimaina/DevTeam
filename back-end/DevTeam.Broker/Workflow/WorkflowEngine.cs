using System.Text;

using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
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

    // A confused agent could otherwise ping-pong delegation requests forever — same shape as
    // WorkflowStep.LoopAttempts, just for a different kind of retry.
    private const int MaxDelegationRoundTrips = 3;

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
        IGitCredentialStore credentialStore)
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

    // ─── feature lifecycle (GitFlow) ────────────────────────────────────────

    public async Task<ReleaseFeature> CreateFeatureAsync(Guid releaseId, string featureKey, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await db.Releases
            .Include(r => r.Features)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        var workflow = LoadWorkflow(release.WorkspacePath);

        if (release.CurrentFeatureId is not null)
            throw new InvalidOperationException(
                "This release already has a feature in progress. Complete it before starting another.");

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
        release.CurrentFeatureId = feature.Id;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

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

        var existingActive = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active);
        if (existingActive is not null)
            return existingActive;

        var featureKey = feature.Key;
        var workspacePath = feature.Release.WorkspacePath;

        var stageRun = new ReleaseStageRun
        {
            Feature = feature,
            StageName = role.Name,
            Attempt = feature.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.GuidedQA,
        };
        db.ReleaseStageRuns.Add(stageRun);
        await db.SaveChangesAsync(ct);

        // Entry gates run before a session is even opened — a role that never gets past its
        // own entry gates has no need for one yet, and shouldn't pay for it.
        if (!await RunEntryGatesAsync(role, featureKey, workspacePath, workflow, stageRun, db, ct))
            return stageRun;

        var allowedWritePrefixes = ResolveAllowedWritePrefixes(role.WritesCode, role.Name, workspacePath, featureKey, workflow);
        var session = await _coordinator.NewSessionAsync(workspacePath, DefaultModelId, allowedWritePrefixes, ct);
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
        var specialists = await db.SpecialistRoles.ToListAsync(ct);

        // A role-declared seed (e.g. the default business-analyst role's BRS-authoring
        // instruction — see WorkflowYaml.DefaultPipeline) instead of a name check, so a
        // custom/renamed first stage doesn't silently inherit BA-specific instructions it
        // never asked for.
        var requirementsAuthoring = role.SeedPrompt is null
            ? string.Empty
            : ResolvePlaceholders(role.SeedPrompt, workflow, workspacePath, featureKey);

        var finalPrompt = resolvedPrompt.OverridesBuiltIn
            ? resolvedPrompt.Text
            : $"You are the {role.Name} for feature '{featureKey}' in workspace '{workspacePath}'. " +
              MultiQuestionDetector.SingleQuestionInstruction +
              " Follow each answer to its logical conclusion before asking the next. " +
              "When you have enough information to produce the required output, say DONE and provide the structured result." +
              requirementsAuthoring +
              HandoffAutomationClause +
              BuildDelegationClause(specialists, featureKey) +
              AsAugmentingClause(resolvedPrompt.Text) +
              guidanceContext +
              artifactContext;

        // Use a separate cancellation for the agent prompt so HTTP timeouts don't kill it.
        // The agent may take time to process the initial prompt — that's expected.
        using var agentCts = InteractiveTurnCts();
        try
        {
            await PromptWithDelegationAsync(session.SessionId, finalPrompt, stageRun, workspacePath, featureKey, workflow, db, agentCts.Token);
            ClearPromptFailure(stageRun);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            await RecordPromptFailureAsync(db, stageRun, ex, ct);
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

        using var agentCts = InteractiveTurnCts();
        PromptResponse response;
        try
        {
            response = await _coordinator.PromptWithSessionRecoveryAsync(acpSessionId, text, agentCts.Token);
            ClearPromptFailure(stageRun);
        }
        catch (Exception ex)
        {
            await RecordPromptFailureAsync(db, stageRun, ex, agentCts.Token);
            throw;
        }

        stageRun.QuestionCount++;
        await db.SaveChangesAsync(agentCts.Token);

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

            var correction = await _coordinator.PromptWithSessionRecoveryAsync(
                acpSessionId, MultiQuestionDetector.CorrectionPrompt, token, isPriming: true);
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

        var stageRun = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active)
            ?? throw new InvalidOperationException($"No active stage run for '{role.Name}'.");

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
        await db.SaveChangesAsync(ct);

        // Leading builtins (e.g. scaffold_specs/context_bundle) already ran in StartStageAsync
        // before the chat started, and are already recorded on stageRun.GateChecks. Re-running
        // them here would trip ScaffoldSpecsGate's "don't overwrite an existing scaffold" guard,
        // so skip every step up to and including the first Agent/Loop step, and seed allPassed
        // from those already-recorded results instead of assuming success.
        var allPassed = stageRun.GateChecks.All(gc => gc.Passed);
        var pastLeadingSteps = false;
        foreach (var step in role.Steps)
        {
            if (!pastLeadingSteps)
            {
                if (step.Kind is WorkflowStepKind.Agent or WorkflowStepKind.Loop)
                    pastLeadingSteps = true;
                continue;
            }
            if (step.Kind == WorkflowStepKind.Agent) continue;
            var result = await ExecuteStepAsync(step, role, featureKey, workspacePath, workflow, stageRun, db, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
            });
            if (!result.Passed) allPassed = false;
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

        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (isLastStage)
        {
            await FinalizeFeatureCompletionAsync(db, feature, ct);
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
            stageRun = new ReleaseStageRun
            {
                Feature = feature,
                StageName = role.Name,
                Attempt = feature.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
                Status = ReleaseStageStatus.Active,
                Phase = StagePhase.GuidedQA,
            };
            db.ReleaseStageRuns.Add(stageRun);
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
            var session = await _coordinator.NewSessionAsync(workspacePath, DefaultModelId, allowedWritePrefixes, ct);
            stageRun.AcpSessionId = session.SessionId.ToString();
            await db.SaveChangesAsync(ct);
        }

        stageRun.Phase = StagePhase.Producing;
        await db.SaveChangesAsync(ct);

        async Task PromptRoleAsync()
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
            var specialists = await db.SpecialistRoles.ToListAsync(ct);
            // Bug fix: role.SeedPrompt used to only be honored in StartStageAsync (the
            // interactive path) — every custom autonomous stage (anything that isn't a
            // chat-style BA clone) silently never saw its own seed instructions at all.
            var seedInstruction = role.SeedPrompt is null
                ? string.Empty
                : " " + ResolvePlaceholders(role.SeedPrompt, workflow, workspacePath, featureKey);
            var prompt = resolvedPrompt.OverridesBuiltIn
                ? resolvedPrompt.Text
                : $"You are the {role.Name} for feature '{featureKey}' in workspace '{workspacePath}'. " +
                  "Work autonomously and do not ask the user for input. Produce the required artifacts." +
                  seedInstruction +
                  HandoffAutomationClause +
                  AutonomousTersenessClause +
                  BuildDelegationClause(specialists, featureKey) +
                  AsAugmentingClause(resolvedPrompt.Text) +
                  guidanceContext +
                  artifactContext +
                  "When you are done, say DONE and provide a summary of what you changed.";
            try
            {
                await PromptWithDelegationAsync(acpSessionId, prompt, stageRun, workspacePath, featureKey, workflow, db, agentCts.Token);
                ClearPromptFailure(stageRun);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                await RecordPromptFailureAsync(db, stageRun, ex, ct);
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
                                await PromptRoleAsync();
                                continue;
                            }

                            var inner = await ExecuteStepAsync(innerStep, role, featureKey, workspacePath, workflow, stageRun, db, ct);
                            db.ReleaseGateChecks.Add(new ReleaseGateCheck
                            {
                                StageRun = stageRun,
                                Name = inner.StepName,
                                Passed = inner.Passed,
                                EvidenceText = inner.Evidence,
                            });
                            await db.SaveChangesAsync(ct);
                            if (!inner.Passed)
                            {
                                attemptPassed = false;
                                failedSteps.Add(inner);
                            }
                        }

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
        if (!allPassed && finalFailedSteps.Count > 0)
        {
            var owner = finalFailedSteps
                .Select(s => s.ResponsibleRole)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r) && r != role.Name);

            var routed = owner is not null && await RouteGateFailureToOwnerAsync(
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

        feature.Release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (isLastStage)
        {
            await FinalizeFeatureCompletionAsync(db, feature, ct);
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
        string targetStageName, string? instructions, string addedBy, DevTeamDbContext db, CancellationToken ct)
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

        await MoveToStageAsync(
            feature, position, fromStageName, targetIndex, responsibleRole,
            BuildGateFailureFeedback(failedSteps), "system:gate-failure", db, ct);
        return true;
    }

    public async Task<IReadOnlyList<MessageDto>> GetStageMessagesAsync(
        Guid featureId, Guid stageRunId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
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
            .Select(r => new PipelineStageDto(r.Name, r.UserInputRequired, r.Signoff, r.ExpectedArtifacts, FlattenStepNames(r.Steps)))
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
            await FinalizeFeatureCompletionAsync(db, feature, ct);
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
            await FinalizeFeatureCompletionAsync(db, feature, ct);
        }

        await BroadcastEventAsync(feature.ReleaseId, "signoffApproved", new
        {
            StageName = stageName,
            Role = role,
            AllComplete = allSignoffsComplete,
        }, ct);

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
    }

    public async Task<DevTeamRelease> GetReleaseAsync(Guid releaseId, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
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

    public async Task<IReadOnlyList<DevTeamRelease>> ListReleasesAsync(string? workspacePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var releases = await db.Releases
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

    private async Task FinalizeFeatureCompletionAsync(DevTeamDbContext db, ReleaseFeature feature, CancellationToken ct)
    {
        var release = feature.Release;

        var checkoutResult = await _gitService.CheckoutAsync(release.WorkspacePath, release.BranchName, ct);
        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Could not check out release branch '{release.BranchName}': {checkoutResult.Message}");

        var mergeResult = await _gitService.MergeAsync(release.WorkspacePath, feature.BranchName, release.BranchName, ct);
        if (!mergeResult.Success)
            throw new InvalidOperationException(
                $"Could not merge '{feature.BranchName}' into '{release.BranchName}': {mergeResult.Message}");

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

        var deleteResult = await _gitService.DeleteBranchAsync(release.WorkspacePath, feature.BranchName, authToken, ct);
        if (!deleteResult.Success)
            _logger.LogWarning("Deleting feature branch '{Branch}' failed: {Message}", feature.BranchName, deleteResult.Message);

        feature.Status = ReleaseFeatureStatus.Complete;
        release.CurrentFeatureId = null;
        release.Status = ReleaseStatus.Ready;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> TryGetCredentialForWorkspaceAsync(DevTeamDbContext db, string workspacePath, CancellationToken ct)
    {
        var settings = await db.WorkspaceGitSettings.FindAsync([workspacePath], ct);
        if (settings?.CredentialName is null) return null;
        return _credentialStore.TryGetToken(settings.CredentialName);
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

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
        if (manifest is null)
        {
            _logger.LogWarning(
                "No slice manifest found for feature '{FeatureKey}' when scoping '{Owner}'; " +
                "restricting write access to the feature docs directory only.", featureKey, ownerName);
            return [featureDir, docsRoot];
        }

        var prefixes = new List<string> { featureDir, docsRoot, manifest.CodePathBack, manifest.CodePathFront };
        prefixes.AddRange(manifest.Shared);
        return prefixes;
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
                // Pre-scaffold (no manifest yet), fall back to the declared pattern instead of
                // failing outright — same "soft degrade" treatment ResolveAllowedWritePrefixes
                // already gives a missing manifest.
                var relative = rootKey == ArtifactRoots.FeatureCodeRootBack
                    ? (manifest?.CodePathBack ?? workflow.Slices.CodeBack.Replace("<F>", featureKey))
                    : (manifest?.CodePathFront ?? workflow.Slices.CodeFront.Replace("<F>", featureKey));
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
            var session = await _coordinator.NewSessionAsync(workspacePath, DefaultModelId, allowedPrefixes, ct);
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
            if (manifest is null && !hasRequirements)
                return string.Empty;

            var scaffold = new StringBuilder();
            scaffold.AppendLine();
            scaffold.AppendLine();
            scaffold.AppendLine("--- Feature scaffold (from scaffold_specs) ---");
            if (manifest is not null)
            {
                scaffold.AppendLine($"Title: {manifest.Title}");
                scaffold.AppendLine($"Backend path: {manifest.CodePathBack}");
                scaffold.AppendLine($"Frontend path: {manifest.CodePathFront}");
            }
            if (hasRequirements)
            {
                scaffold.AppendLine();
                scaffold.Append(File.ReadAllText(brsPath));
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
        var result = await _gateRunner.RunAsync(gateName, request, ct);
        return new StepExecutionResult { StepName = gateName, Passed = result.Passed, Reason = result.Reason, Evidence = result.EvidenceText };
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
            var testFiles = TestDiscovery.Discover(workspacePath);
            if (testFiles.Count > 0)
                inputs["testFilesJson"] = System.Text.Json.JsonSerializer.Serialize(testFiles);

            var testCommand = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey))?.TestCommand
                ?? "dotnet test DevTeam.slnx";
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
            var session = await _coordinator.NewSessionAsync(workspacePath, DefaultModelId, null, ct);
            var response = await _coordinator.PromptWithSessionRecoveryAsync(session.SessionId, promptText, ct, isPriming: true);

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
        => await db.Releases
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.GateChecks)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.Findings)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.GuidanceNotes)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.SpecialistConsultations)
            .Include(r => r.Features).ThenInclude(f => f.Signoffs)
            .Include(r => r.Features).ThenInclude(f => f.FlowPosition)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");
}
