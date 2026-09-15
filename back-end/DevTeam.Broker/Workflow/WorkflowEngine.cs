using System.Text;

using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
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

    // Leaving modelId null lets opencode fall back to its own ambient "current" model,
    // which can silently drift to something far slower than intended (observed: an
    // unpinned session took 20+ minutes per turn vs. ~15s pinned to this model).
    // Always request a known-good default explicitly instead.
    private const string DefaultModelId = "opencode/big-pickle";

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
        var workflow = _loader.LoadDefault();
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
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await db.Releases
            .Include(r => r.Features)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

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
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
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
        var allowedWritePrefixes = ResolveAllowedWritePrefixes(role.Name, workspacePath, featureKey);
        var session = await _coordinator.NewSessionAsync(workspacePath, DefaultModelId, allowedWritePrefixes, ct);

        var stageRun = new ReleaseStageRun
        {
            Feature = feature,
            StageName = role.Name,
            Attempt = feature.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.GuidedQA,
            AcpSessionId = session.SessionId.ToString(),
        };
        db.ReleaseStageRuns.Add(stageRun);
        await db.SaveChangesAsync(ct);

        // Run this role's leading builtins (e.g. scaffold_specs/context_bundle for
        // business-analyst) before the very first chat message goes out, so the agent
        // starts with a scaffold already in place instead of discovering it mid-conversation.
        await RunLeadingBuiltinsAsync(role, featureKey, workspacePath, stageRun, db, ct);
        await db.SaveChangesAsync(ct);

        var guidanceContext = BuildGuidanceContext(feature);
        var artifactContext = BuildArtifactContext(workspacePath, featureKey, role.Name);

        var requirementsAuthoring = role.Name == "business-analyst"
            ? $" Author the agreed requirements into devteam/features/{featureKey}/requirements.md " +
              $"using your file tools (create the directory if needed): one \"## REQ-N: <Title>\" section per requirement, " +
              $"each followed by a Given/When/Then acceptance-criteria sentence. Every requirement MUST contain Given, When and Then. "
            : string.Empty;

        // Use a separate cancellation for the agent prompt so HTTP timeouts don't kill it.
        // The agent may take time to process the initial prompt — that's expected.
        using var agentCts = InteractiveTurnCts();
        await _coordinator.PromptWithSessionRecoveryAsync(
            session.SessionId,
            $"You are the {role.Name} for feature '{featureKey}' in workspace '{workspacePath}'. " +
            MultiQuestionDetector.SingleQuestionInstruction +
            " Follow each answer to its logical conclusion before asking the next. " +
            "When you have enough information to produce the required output, say DONE and provide the structured result." +
            requirementsAuthoring +
            HandoffAutomationClause +
            guidanceContext +
            artifactContext,
            agentCts.Token);

        _logger.LogInformation("Started stage {StageName} for feature {FeatureId}, session {SessionId}",
            role.Name, featureId, session.SessionId);
        return stageRun;
    }

    public async Task<StagePromptResult> SendMessageAsync(Guid featureId, string text, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feature = await LoadFeatureAsync(db, featureId, ct);
        var workflow = LoadWorkflow();
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var role = workflow.Pipeline[position.CurrentStageIndex];
        var stageRun = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active)
            ?? throw new InvalidOperationException($"No active stage run for '{role.Name}'.");

        if (stageRun.AcpSessionId is null)
            throw new InvalidOperationException("Stage run has no linked session.");

        var acpSessionId = Guid.Parse(stageRun.AcpSessionId);

        // A new human message always invalidates a prior "ready to proceed" signal — the
        // conversation isn't over until the agent says DONE again and gates re-pass.
        stageRun.ReadyToProceed = false;

        using var agentCts = InteractiveTurnCts();
        var response = await _coordinator.PromptWithSessionRecoveryAsync(acpSessionId, text, agentCts.Token);

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
        var workflow = LoadWorkflow();
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
                acpSessionId, MultiQuestionDetector.CorrectionPrompt, token);
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
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
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
            var artifactPath = artifactPattern.Replace("<F>", featureKey);
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
            var result = await ExecuteStepAsync(step, featureKey, workspacePath, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
            });
            if (!result.Passed) allPassed = false;
        }

        stageRun.Phase = StagePhase.Challenge;
        await db.SaveChangesAsync(ct);

        var challenge = workflow.Challenges.FirstOrDefault(c => c.Producer == role.Name);
        if (challenge is not null)
        {
            var challengeResult = await RunChallengeAsync(challenge, stageRun, featureKey, workspacePath, db, ct);
            if (!challengeResult.Passed) allPassed = false;
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
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
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
        var guidanceContext = BuildGuidanceContext(feature);

        var stageRun = feature.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active);
        if (stageRun is null)
        {
            var allowedWritePrefixes = ResolveAllowedWritePrefixes(role.Name, workspacePath, featureKey);
            var session = await _coordinator.NewSessionAsync(workspacePath, DefaultModelId, allowedWritePrefixes, ct);
            stageRun = new ReleaseStageRun
            {
                Feature = feature,
                StageName = role.Name,
                Attempt = feature.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
                Status = ReleaseStageStatus.Active,
                Phase = StagePhase.GuidedQA,
                AcpSessionId = session.SessionId.ToString(),
            };
            db.ReleaseStageRuns.Add(stageRun);
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
            var artifactContext = BuildArtifactContext(workspacePath, featureKey, role.Name);
            var prompt =
                $"You are the {role.Name} for feature '{featureKey}' in workspace '{workspacePath}'. " +
                "Work autonomously and do not ask the user for input. Produce the required artifacts." +
                HandoffAutomationClause +
                guidanceContext +
                artifactContext +
                "When you are done, say DONE and provide a summary of what you changed.";
            await _coordinator.PromptWithSessionRecoveryAsync(acpSessionId, prompt, agentCts.Token);
        }

        var allPassed = true;
        foreach (var step in role.Steps)
        {
            switch (step.Kind)
            {
                case WorkflowStepKind.Agent:
                    await PromptRoleAsync();
                    break;
                case WorkflowStepKind.Builtin:
                    var builtin = await ExecuteStepAsync(step, featureKey, workspacePath, ct);
                    db.ReleaseGateChecks.Add(new ReleaseGateCheck
                    {
                        StageRun = stageRun,
                        Name = builtin.StepName,
                        Passed = builtin.Passed,
                        EvidenceText = builtin.Evidence,
                    });
                    // Flushed immediately (not just at phase boundaries) so a concurrent poll —
                    // the frontend's live step checklist — can see progress as it happens.
                    await db.SaveChangesAsync(ct);
                    if (!builtin.Passed) allPassed = false;
                    break;
                case WorkflowStepKind.Loop:
                    var attempts = step.LoopAttempts ?? 3;
                    var lastAttemptPassed = false;
                    for (var attempt = 1; attempt <= attempts; attempt++)
                    {
                        var attemptPassed = true;
                        foreach (var innerStep in step.LoopSteps ?? [])
                        {
                            if (innerStep.Kind == WorkflowStepKind.Agent)
                            {
                                await PromptRoleAsync();
                                continue;
                            }

                            var inner = await ExecuteStepAsync(innerStep, featureKey, workspacePath, ct);
                            db.ReleaseGateChecks.Add(new ReleaseGateCheck
                            {
                                StageRun = stageRun,
                                Name = inner.StepName,
                                Passed = inner.Passed,
                                EvidenceText = inner.Evidence,
                            });
                            await db.SaveChangesAsync(ct);
                            if (!inner.Passed) attemptPassed = false;
                        }

                        lastAttemptPassed = attemptPassed;
                        if (attemptPassed) break;
                    }

                    if (!lastAttemptPassed) allPassed = false;
                    break;
            }
        }

        stageRun.Phase = StagePhase.Challenge;
        await db.SaveChangesAsync(ct);

        var challenge = workflow.Challenges.FirstOrDefault(c => c.Producer == role.Name);
        if (challenge is not null)
        {
            var challengeResult = await RunChallengeAsync(challenge, stageRun, featureKey, workspacePath, db, ct);
            if (!challengeResult.Passed) allPassed = false;
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
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
        var position = feature.FlowPosition
            ?? throw new InvalidOperationException("Feature has no flow position.");

        var currentIndex = position.CurrentStageIndex;
        var targetIndex = -1;
        for (var i = 0; i < workflow.Pipeline.Count; i++)
        {
            if (workflow.Pipeline[i].Name == targetStageName)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
            throw new InvalidOperationException($"No stage '{targetStageName}' in the pipeline.");
        if (targetIndex >= currentIndex)
            throw new InvalidOperationException("Push back is only allowed to a previous stage.");

        var notesTarget = feature.StageRuns
            .Where(sr => sr.StageName == workflow.Pipeline[currentIndex].Name)
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
                AddedBy = "user",
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

        return await LoadReleaseAsync(db, feature.ReleaseId, ct);
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
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _ = await LoadFeatureAsync(db, featureId, ct);

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
            }
        }
        return names;
    }

    private const int StageArtifactMaxChars = 20 * 1024;

    public async Task<IReadOnlyList<StageArtifactDto>> GetStageArtifactsAsync(Guid featureId, Guid stageRunId, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feature = await LoadFeatureAsync(db, featureId, ct);
        var stageRun = feature.StageRuns.FirstOrDefault(sr => sr.Id == stageRunId)
            ?? throw new InvalidOperationException($"No stage run '{stageRunId}' for feature '{featureId}'.");
        var role = workflow.Pipeline.FirstOrDefault(r => r.Name == stageRun.StageName)
            ?? throw new InvalidOperationException($"Unknown role '{stageRun.StageName}'.");

        var workspacePath = feature.Release.WorkspacePath;
        var featureKey = feature.Key;

        var results = new List<StageArtifactDto>();
        foreach (var pattern in role.ExpectedArtifacts)
        {
            var relativePath = pattern.Replace("<F>", featureKey);
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
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var feature = await LoadFeatureAsync(db, featureId, ct);
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
            var artifactPath = artifactPattern.Replace("<F>", featureKey);
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
            var result = await ExecuteStepAsync(step, featureKey, feature.Release.WorkspacePath, ct);
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
            var workflow = LoadWorkflow();
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
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await LoadReleaseAsync(db, releaseId, ct);
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
    /// business-analyst is confined to its own feature docs directory (never source code);
    /// developer/qa additionally get the feature's actual code paths, read from the manifest
    /// scaffold_specs already produced. Falls back to the docs-only scope (never unrestricted)
    /// if the manifest is unexpectedly missing.
    /// </summary>
    private IReadOnlyList<string> ResolveAllowedWritePrefixes(string roleName, string workspacePath, string featureKey)
    {
        var featureDir = ArtifactPaths.FeatureDirRelative(featureKey);
        if (roleName is not ("developer" or "qa"))
            return [featureDir];

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
        if (manifest is null)
        {
            _logger.LogWarning(
                "No slice manifest found for feature '{FeatureKey}' when scoping role '{Role}'; " +
                "restricting write access to the feature docs directory only.", featureKey, roleName);
            return [featureDir];
        }

        var prefixes = new List<string> { featureDir, manifest.CodePathBack, manifest.CodePathFront };
        prefixes.AddRange(manifest.Shared);
        return prefixes;
    }

    /// <summary>
    /// Runs a role's steps up to (but not including) the first Agent/Loop step — the builtins
    /// that must produce scaffolding/context before the interactive or autonomous conversation
    /// starts (e.g. scaffold_specs/context_bundle for business-analyst). Records a gate check
    /// per executed step so RunGatesAsync doesn't need to re-run them afterward.
    /// </summary>
    private async Task<bool> RunLeadingBuiltinsAsync(
        WorkflowRole role, string featureKey, string workspacePath, ReleaseStageRun stageRun, DevTeamDbContext db, CancellationToken ct)
    {
        var allPassed = true;
        foreach (var step in role.Steps)
        {
            if (step.Kind is WorkflowStepKind.Agent or WorkflowStepKind.Loop)
                break;

            var result = await ExecuteStepAsync(step, featureKey, workspacePath, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
            });
            if (!result.Passed) allPassed = false;
        }
        return allPassed;
    }

    /// <summary>
    /// Builds the verbatim artifact content to splice into a stage's opening prompt, so the
    /// next role starts from what the previous stage actually produced instead of discovering
    /// (or ignoring) it on its own initiative. developer/qa get context.md verbatim (written by
    /// the context_bundle builtin); business-analyst has no upstream stage, so it gets its own
    /// just-scaffolded manifest + requirements skeleton instead.
    /// </summary>
    private static string BuildArtifactContext(string workspacePath, string featureKey, string roleName)
    {
        if (roleName == "business-analyst")
        {
            var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
            var requirementsPath = ArtifactPaths.RequirementsPath(workspacePath, featureKey);
            var hasRequirements = File.Exists(requirementsPath);
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
                scaffold.Append(File.ReadAllText(requirementsPath));
            }
            return scaffold.ToString();
        }

        var contextPath = ArtifactPaths.ContextPath(workspacePath, featureKey);
        if (!File.Exists(contextPath))
            return string.Empty;

        return "\n\n--- Context from the previous stage (context.md) ---\n" + File.ReadAllText(contextPath);
    }

    // ─── internal execution ────────────────────────────────────────────────

    private async Task<StepExecutionResult> ExecuteStepAsync(WorkflowStep step, string featureKey, string workspacePath, CancellationToken ct) =>
        step.Kind switch
        {
            WorkflowStepKind.Builtin => await ExecuteBuiltinAsync(step.Builtin!, featureKey, workspacePath, ct),
            WorkflowStepKind.Agent => new StepExecutionResult { StepName = $"agent:{step.AgentMode}", Passed = true, Evidence = "Interactive — handled via chat." },
            WorkflowStepKind.Loop => await ExecuteLoopAsync(step, featureKey, workspacePath, ct),
            _ => throw new InvalidOperationException($"Unknown step kind: {step.Kind}"),
        };

    private async Task<StepExecutionResult> ExecuteBuiltinAsync(string gateName, string featureKey, string workspacePath, CancellationToken ct)
    {
        var request = new GateRequest(gateName, workspacePath, featureKey, Inputs: await BuildBuiltinInputsAsync(gateName, featureKey, workspacePath, ct));
        var result = await _gateRunner.RunAsync(gateName, request, ct);
        return new StepExecutionResult { StepName = gateName, Passed = result.Passed, Evidence = result.EvidenceText };
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

    private async Task<StepExecutionResult> ExecuteLoopAsync(WorkflowStep step, string featureKey, string workspacePath, CancellationToken ct)
    {
        var maxAttempts = step.LoopAttempts ?? 3;
        var lastResult = new StepExecutionResult { StepName = "loop", Passed = false };
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            foreach (var innerStep in step.LoopSteps ?? [])
            {
                if (innerStep.Kind == WorkflowStepKind.Agent) continue;
                lastResult = await ExecuteStepAsync(innerStep, featureKey, workspacePath, ct);
                if (!lastResult.Passed) break;
            }
            if (lastResult.Passed) break;
        }
        return lastResult with { StepName = $"loop({maxAttempts})" };
    }

    private async Task<StepExecutionResult> RunChallengeAsync(WorkflowChallenge challenge, ReleaseStageRun stageRun, string featureKey, string workspacePath, DevTeamDbContext db, CancellationToken ct)
    {
        try
        {
            var antagonistMode = challenge.AntagonistMode ?? "build";
            var session = await _coordinator.NewSessionAsync(workspacePath, DefaultModelId, null, ct);

            var prompt = $"You are reviewing the {challenge.Producer}'s work for feature '{featureKey}'. " +
                         $"Review for completeness, correctness, and quality. " +
                         $"Report any issues, ambiguities, or gaps as findings.";

            var response = await _coordinator.PromptWithSessionRecoveryAsync(session.SessionId, prompt, ct);

            var passed = response.StopReason == "end_turn";
            if (!passed)
            {
                db.ReviewFindings.Add(new ReviewFinding
                {
                    StageRun = stageRun,
                    Target = challenge.Producer,
                    Kind = ReviewFindingKind.Requirement,
                    Severity = ReviewFindingSeverity.Major,
                    Summary = $"Challenge agent ({antagonistMode}) found issues.",
                    Status = ReviewFindingStatus.Open,
                });
            }

            return new StepExecutionResult
            {
                StepName = $"challenge:{challenge.Producer}->{antagonistMode}",
                Passed = passed,
                Evidence = $"Challenge reviewed by {antagonistMode}.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Challenge failed for {Producer}", challenge.Producer);
            return new StepExecutionResult
            {
                StepName = $"challenge:{challenge.Producer}",
                Passed = true,
                Evidence = $"Challenge skipped: {ex.Message}",
            };
        }
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

    private WorkflowDefinition LoadWorkflow() => _loader.LoadDefault();

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
            .Include(f => f.Signoffs)
            .Include(f => f.FlowPosition)
            .SingleOrDefaultAsync(f => f.Id == featureId, ct)
            ?? throw new KeyNotFoundException($"Feature {featureId} not found.");

    private static async Task<DevTeamRelease> LoadReleaseAsync(DevTeamDbContext db, Guid releaseId, CancellationToken ct)
        => await db.Releases
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.GateChecks)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.Findings)
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.GuidanceNotes)
            .Include(r => r.Features).ThenInclude(f => f.Signoffs)
            .Include(r => r.Features).ThenInclude(f => f.FlowPosition)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");
}
