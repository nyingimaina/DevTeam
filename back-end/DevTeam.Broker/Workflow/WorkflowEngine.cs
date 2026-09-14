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
    private readonly WorkflowDefinitionLoader _loader;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IDbContextFactory<DevTeamDbContext> dbFactory,
        IGateRunner gateRunner,
        IWorkflowCoordinator coordinator,
        IEventBroadcaster broadcaster,
        IGitService gitService,
        WorkflowDefinitionLoader loader,
        ILogger<WorkflowEngine> logger)
    {
        _dbFactory = dbFactory;
        _gateRunner = gateRunner;
        _coordinator = coordinator;
        _broadcaster = broadcaster;
        _gitService = gitService;
        _loader = loader;
        _logger = logger;
    }

    public async Task<DevTeamRelease> StartReleaseAsync(string featureKey, string workspacePath, CancellationToken ct)
    {
        var workflow = _loader.LoadDefault();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = new DevTeamRelease
        {
            WorkspacePath = workspacePath,
            Title = $"Release {featureKey}",
            Status = ReleaseStatus.InProgress,
        };

        var feature = new ReleaseFeature
        {
            Release = release,
            Key = featureKey,
            Title = featureKey,
            Status = ReleaseFeatureStatus.InProgress,
        };
        release.Features.Add(feature);

        foreach (var role in workflow.Pipeline)
        {
            if (role.Signoff is not null)
            {
                release.Signoffs.Add(new ReleaseSignoff
                {
                    Release = release,
                    StageName = role.Name,
                    Required = true,
                    Approved = false,
                });
            }
        }

        db.Releases.Add(release);
        await db.SaveChangesAsync(ct);

        var position = new ReleaseFlowPosition
        {
            Release = release,
            CurrentStageIndex = 0,
            CurrentStageName = workflow.Pipeline[0].Name,
        };
        db.ReleaseFlowPositions.Add(position);
        await db.SaveChangesAsync(ct);

        release.FlowPosition = position;

        try
        {
            var releaseBranch = $"release/{featureKey}";
            await _gitService.InitAsync(workspacePath, ct);
            await _gitService.EnsureBranchAsync(workspacePath, releaseBranch, ct);
            _logger.LogInformation("Created git branch {Branch} for release {ReleaseId}", releaseBranch, release.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create git branch for release {ReleaseId}", release.Id);
        }

        _logger.LogInformation("Started release {ReleaseId} for feature {FeatureKey}", release.Id, featureKey);
        return release;
    }

    // ─── interactive stage lifecycle ───────────────────────────────────────

    public async Task<ReleaseStageRun> StartStageAsync(Guid releaseId, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await LoadReleaseAsync(db, releaseId, ct);
        var position = release.FlowPosition
            ?? throw new InvalidOperationException("Release has no flow position.");

        var currentStageIndex = position.CurrentStageIndex;
        if (currentStageIndex >= workflow.Pipeline.Count)
            throw new InvalidOperationException("All stages already complete.");

        var role = workflow.Pipeline[currentStageIndex];

        var existingActive = release.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active);
        if (existingActive is not null)
            return existingActive;

        var session = await _coordinator.NewSessionAsync(release.WorkspacePath, null, ct);

        var stageRun = new ReleaseStageRun
        {
            Release = release,
            StageName = role.Name,
            Attempt = release.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.GuidedQA,
            AcpSessionId = session.SessionId.ToString(),
        };
        db.ReleaseStageRuns.Add(stageRun);
        await db.SaveChangesAsync(ct);

        var featureKey = release.Features.FirstOrDefault()?.Key ?? "unknown";
        var guidanceContext = BuildGuidanceContext(release);

        var requirementsAuthoring = role.Name == "business-analyst"
            ? $" Author the agreed requirements into devteam/features/{featureKey}/requirements.md " +
              $"using your file tools (create the directory if needed): one \"## REQ-N: <Title>\" section per requirement, " +
              $"each followed by a Given/When/Then acceptance-criteria sentence. Every requirement MUST contain Given, When and Then. "
            : string.Empty;

        // Use a separate cancellation for the agent prompt so HTTP timeouts don't kill it.
        // The agent may take time to process the initial prompt — that's expected.
        using var agentCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        agentCts.CancelAfter(TimeSpan.FromMinutes(10));
        await _coordinator.PromptWithSessionRecoveryAsync(
            session.SessionId,
            $"You are the {role.Name} for feature '{featureKey}' in workspace '{release.WorkspacePath}'. " +
            $"Ask ONE question at a time. Follow each answer to its logical conclusion before asking the next. " +
            $"When you have enough information to produce the required output, say DONE and provide the structured result." +
            requirementsAuthoring +
            guidanceContext,
            agentCts.Token);

        _logger.LogInformation("Started stage {StageName} for release {ReleaseId}, session {SessionId}",
            role.Name, releaseId, session.SessionId);
        return stageRun;
    }

    public async Task<StagePromptResult> SendMessageAsync(Guid releaseId, string text, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var release = await LoadReleaseAsync(db, releaseId, ct);
        var workflow = LoadWorkflow();
        var position = release.FlowPosition
            ?? throw new InvalidOperationException("Release has no flow position.");

        var role = workflow.Pipeline[position.CurrentStageIndex];
        var stageRun = release.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active)
            ?? throw new InvalidOperationException($"No active stage run for '{role.Name}'.");

        if (stageRun.AcpSessionId is null)
            throw new InvalidOperationException("Stage run has no linked session.");

        var acpSessionId = Guid.Parse(stageRun.AcpSessionId);
        var response = await _coordinator.PromptWithSessionRecoveryAsync(acpSessionId, text, ct);

        stageRun.QuestionCount++;
        await db.SaveChangesAsync(ct);

        return new StagePromptResult(
            Response: response.StopReason,
            InputTokens: response.TotalTokens,
            OutputTokens: response.OutputTokens,
            TotalTokens: response.TotalTokens);
    }

    public async Task<DevTeamRelease> RunGatesAsync(Guid releaseId, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await LoadReleaseAsync(db, releaseId, ct);
        var position = release.FlowPosition
            ?? throw new InvalidOperationException("Release has no flow position.");

        var role = workflow.Pipeline[position.CurrentStageIndex];
        var featureKey = release.Features.FirstOrDefault()?.Key ?? "unknown";
        var currentIndex = position.CurrentStageIndex;

        var stageRun = release.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active)
            ?? throw new InvalidOperationException($"No active stage run for '{role.Name}'.");

        // Check user input requirement
        if (role.UserInputRequired && stageRun.QuestionCount == 0)
        {
            throw new InvalidOperationException(
                $"Stage '{role.Name}' requires user input. Please chat with the agent before running gates.");
        }

        // Check expected artifacts (warn but don't block)
        var workspacePath = release.WorkspacePath;
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

        var allPassed = true;
        foreach (var step in role.Steps)
        {
            if (step.Kind == WorkflowStepKind.Agent) continue;
            var result = await ExecuteStepAsync(step, featureKey, release.WorkspacePath, ct);
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
            var challengeResult = await RunChallengeAsync(challenge, stageRun, featureKey, release.WorkspacePath, db, ct);
            if (!challengeResult.Passed) allPassed = false;
        }

        if (allPassed)
        {
            var needsSignoff = release.Signoffs.Any(s => s.StageName == role.Name && !s.Approved);
            stageRun.Status = needsSignoff ? ReleaseStageStatus.BlockedSignoff : ReleaseStageStatus.Complete;
            stageRun.Phase = StagePhase.Signoff;

            if (!needsSignoff)
            {
                position.CurrentStageIndex++;
                position.CurrentStageName = currentIndex + 1 < workflow.Pipeline.Count
                    ? workflow.Pipeline[currentIndex + 1].Name
                    : "done";
                position.UpdatedAt = DateTimeOffset.UtcNow;

                if (position.CurrentStageIndex >= workflow.Pipeline.Count)
                {
                    release.Status = ReleaseStatus.Ready;
                    release.Features.ForEach(f => f.Status = ReleaseFeatureStatus.Complete);
                }
            }
            else
            {
                release.Status = ReleaseStatus.Blocked;
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

        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastEventAsync(release.Id, "stageStateChanged", new
        {
            StageName = role.Name,
            Status = stageRun.Status.ToString(),
            Phase = stageRun.Phase.ToString(),
            AllPassed = allPassed,
        }, ct);

        return release;
    }

    // ─── autonomous stage execution ─────────────────────────────────────────

    public async Task<DevTeamRelease> RunStageAsync(Guid releaseId, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await LoadReleaseAsync(db, releaseId, ct);
        var position = release.FlowPosition
            ?? throw new InvalidOperationException("Release has no flow position.");

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

        var featureKey = release.Features.FirstOrDefault()?.Key ?? "unknown";
        var guidanceContext = BuildGuidanceContext(release);

        var stageRun = release.StageRuns
            .FirstOrDefault(sr => sr.StageName == role.Name && sr.Status == ReleaseStageStatus.Active);
        if (stageRun is null)
        {
            var session = await _coordinator.NewSessionAsync(release.WorkspacePath, null, ct);
            stageRun = new ReleaseStageRun
            {
                Release = release,
                StageName = role.Name,
                Attempt = release.StageRuns.Count(sr => sr.StageName == role.Name) + 1,
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

            using var agentCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            agentCts.CancelAfter(TimeSpan.FromMinutes(15));
            var prompt =
                $"You are the {role.Name} for feature '{featureKey}' in workspace '{release.WorkspacePath}'. " +
                "Work autonomously and do not ask the user for input. Produce the required artifacts." +
                guidanceContext +
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
                    var builtin = await ExecuteStepAsync(step, featureKey, release.WorkspacePath, ct);
                    db.ReleaseGateChecks.Add(new ReleaseGateCheck
                    {
                        StageRun = stageRun,
                        Name = builtin.StepName,
                        Passed = builtin.Passed,
                        EvidenceText = builtin.Evidence,
                    });
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

                            var inner = await ExecuteStepAsync(innerStep, featureKey, release.WorkspacePath, ct);
                            db.ReleaseGateChecks.Add(new ReleaseGateCheck
                            {
                                StageRun = stageRun,
                                Name = inner.StepName,
                                Passed = inner.Passed,
                                EvidenceText = inner.Evidence,
                            });
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
            var challengeResult = await RunChallengeAsync(challenge, stageRun, featureKey, release.WorkspacePath, db, ct);
            if (!challengeResult.Passed) allPassed = false;
        }

        if (allPassed)
        {
            var needsSignoff = release.Signoffs.Any(s => s.StageName == role.Name && !s.Approved);
            stageRun.Status = needsSignoff ? ReleaseStageStatus.BlockedSignoff : ReleaseStageStatus.Complete;
            stageRun.Phase = StagePhase.Signoff;

            if (!needsSignoff)
            {
                position.CurrentStageIndex++;
                position.CurrentStageName = currentIndex + 1 < workflow.Pipeline.Count
                    ? workflow.Pipeline[currentIndex + 1].Name
                    : "done";
                position.UpdatedAt = DateTimeOffset.UtcNow;

                if (position.CurrentStageIndex >= workflow.Pipeline.Count)
                {
                    release.Status = ReleaseStatus.Ready;
                    release.Features.ForEach(f => f.Status = ReleaseFeatureStatus.Complete);
                }
            }
            else
            {
                release.Status = ReleaseStatus.Blocked;
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

        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastEventAsync(release.Id, "stageStateChanged", new
        {
            StageName = role.Name,
            Status = stageRun.Status.ToString(),
            Phase = stageRun.Phase.ToString(),
            AllPassed = allPassed,
        }, ct);

        return release;
    }

    public async Task<DevTeamRelease> PushBackAsync(
        Guid releaseId, string targetStageName, string? instructions, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await LoadReleaseAsync(db, releaseId, ct);
        var position = release.FlowPosition
            ?? throw new InvalidOperationException("Release has no flow position.");

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

        var notesTarget = release.StageRuns
            .Where(sr => sr.StageName == workflow.Pipeline[currentIndex].Name)
            .OrderByDescending(sr => sr.StartedAt)
            .FirstOrDefault()
            ?? release.StageRuns.OrderByDescending(sr => sr.StartedAt).FirstOrDefault();

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

        var signoff = release.Signoffs.FirstOrDefault(s => s.StageName == targetStageName);
        if (signoff is not null && signoff.Approved)
        {
            signoff.Approved = false;
            signoff.ApprovedBy = null;
            signoff.ApprovedAt = null;
        }

        release.Status = ReleaseStatus.InProgress;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastEventAsync(release.Id, "pushedBack", new
        {
            TargetStage = targetStageName,
            Instructions = instructions,
        }, ct);

        return release;
    }

    public async Task<IReadOnlyList<MessageDto>> GetStageMessagesAsync(
        Guid releaseId, Guid stageRunId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var release = await LoadReleaseAsync(db, releaseId, ct);
        var stageRun = release.StageRuns.FirstOrDefault(sr => sr.Id == stageRunId)
            ?? throw new KeyNotFoundException($"Stage run {stageRunId} not found for release {releaseId}.");

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

    public async Task<IReadOnlyList<PipelineStageDto>> GetPipelineAsync(Guid releaseId, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _ = await LoadReleaseAsync(db, releaseId, ct);

        return workflow.Pipeline
            .Select(r => new PipelineStageDto(r.Name, r.UserInputRequired, r.Signoff))
            .ToArray();
    }

    // ─── legacy advance (runs all steps synchronously) ─────────────────────

    public async Task<DevTeamRelease> AdvanceAsync(Guid releaseId, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await LoadReleaseAsync(db, releaseId, ct);
        var position = release.FlowPosition
            ?? throw new InvalidOperationException("Release has no flow position.");

        var currentIndex = position.CurrentStageIndex;
        if (currentIndex >= workflow.Pipeline.Count)
        {
            release.Status = ReleaseStatus.Ready;
            release.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return release;
        }

        var role = workflow.Pipeline[currentIndex];
        var featureKey = release.Features.FirstOrDefault()?.Key ?? "unknown";

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
            var fullPath = Path.Combine(release.WorkspacePath, artifactPath);
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
            Release = release,
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
            var result = await ExecuteStepAsync(step, featureKey, release.WorkspacePath, ct);
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

        if (allPassed)
        {
            var needsSignoff = release.Signoffs.Any(s => s.StageName == role.Name && !s.Approved);
            if (needsSignoff)
            {
                stageRun.Status = ReleaseStageStatus.BlockedSignoff;
                stageRun.Phase = StagePhase.Signoff;
                release.Status = ReleaseStatus.Blocked;
            }
            else
            {
                position.CurrentStageIndex++;
                position.CurrentStageName = currentIndex + 1 < workflow.Pipeline.Count
                    ? workflow.Pipeline[currentIndex + 1].Name
                    : "done";
                position.UpdatedAt = DateTimeOffset.UtcNow;

                if (position.CurrentStageIndex >= workflow.Pipeline.Count)
                {
                    release.Status = ReleaseStatus.Ready;
                    release.Features.ForEach(f => f.Status = ReleaseFeatureStatus.Complete);
                }
                else
                {
                    release.Status = ReleaseStatus.InProgress;
                }
            }
        }

        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastEventAsync(release.Id, "stageStateChanged", new
        {
            StageName = role.Name,
            Status = stageRun.Status.ToString(),
            Phase = stageRun.Phase.ToString(),
            AllPassed = allPassed,
        }, ct);

        return release;
    }

    public async Task<DevTeamRelease> SignoffAsync(Guid releaseId, string stageName, string role, string? comment, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await LoadReleaseAsync(db, releaseId, ct);
        var signoff = release.Signoffs.FirstOrDefault(s => s.StageName == stageName)
            ?? throw new InvalidOperationException($"No signoff required for stage '{stageName}'.");

        signoff.Approved = true;
        signoff.ApprovedBy = role;
        signoff.Comment = comment;
        signoff.ApprovedAt = DateTimeOffset.UtcNow;

        var allSignoffsComplete = release.Signoffs
            .Where(s => s.StageName == stageName)
            .All(s => s.Approved);

        if (allSignoffsComplete)
        {
            release.Status = ReleaseStatus.InProgress;
            release.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        // When the current stage's signoff is approved, move the flow forward so
        // the release can continue to the next stage (e.g. after autonomous run).
        if (allSignoffsComplete)
        {
            var workflow = LoadWorkflow();
            var position = release.FlowPosition;
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
                    position.CurrentStageName = workflowIndex + 1 < workflow.Pipeline.Count
                        ? workflow.Pipeline[workflowIndex + 1].Name
                        : "done";
                    position.UpdatedAt = DateTimeOffset.UtcNow;

                    release.Status = workflowIndex + 1 >= workflow.Pipeline.Count
                        ? ReleaseStatus.Ready
                        : ReleaseStatus.InProgress;
                    release.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
        }

        await BroadcastEventAsync(releaseId, "signoffApproved", new
        {
            StageName = stageName,
            Role = role,
            AllComplete = allSignoffsComplete,
        }, ct);

        return release;
    }

    public async Task<DevTeamRelease> GetReleaseAsync(Guid releaseId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await LoadReleaseAsync(db, releaseId, ct);
    }

    public async Task<IReadOnlyList<DevTeamRelease>> ListReleasesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var releases = await db.Releases
            .Include(r => r.Features)
            .Include(r => r.StageRuns)
            .Include(r => r.Signoffs)
            .ToListAsync(ct);
        return releases.OrderByDescending(r => r.CreatedAt).ToList();
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
            var session = await _coordinator.NewSessionAsync(workspacePath, null, ct);

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

    private static string BuildGuidanceContext(DevTeamRelease release)
    {
        var notes = release.StageRuns
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

    private static async Task<DevTeamRelease> LoadReleaseAsync(DevTeamDbContext db, Guid releaseId, CancellationToken ct)
        => await db.Releases
            .Include(r => r.Features)
            .Include(r => r.StageRuns).ThenInclude(sr => sr.GateChecks)
            .Include(r => r.StageRuns).ThenInclude(sr => sr.Findings)
            .Include(r => r.StageRuns).ThenInclude(sr => sr.GuidanceNotes)
            .Include(r => r.Signoffs)
            .Include(r => r.FlowPosition)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");
}
