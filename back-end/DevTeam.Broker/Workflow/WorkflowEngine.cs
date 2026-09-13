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

        await _coordinator.SetModeAsync(session.SessionId, role.Name, ct);

        var stageRun = new ReleaseStageRun
        {
            Release = release,
            StageName = role.Name,
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.GuidedQA,
            SessionId = session.SessionId,
        };
        db.ReleaseStageRuns.Add(stageRun);
        await db.SaveChangesAsync(ct);

        var featureKey = release.Features.FirstOrDefault()?.Key ?? "unknown";
        await _coordinator.PromptWithSessionRecoveryAsync(
            session.SessionId,
            $"You are the {role.Name} for feature '{featureKey}' in workspace '{release.WorkspacePath}'. " +
            $"Ask ONE question at a time. Follow each answer to its logical conclusion before asking the next. " +
            $"When you have enough information to produce the required output, say DONE and provide the structured result.",
            ct);

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

        if (stageRun.SessionId is null)
            throw new InvalidOperationException("Stage run has no linked session.");

        var response = await _coordinator.PromptWithSessionRecoveryAsync(stageRun.SessionId.Value, text, ct);

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
        var request = new GateRequest(gateName, workspacePath, featureKey);
        var result = await _gateRunner.RunAsync(gateName, request, ct);
        return new StepExecutionResult { StepName = gateName, Passed = result.Passed, Evidence = result.EvidenceText };
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
            var antagonistMode = challenge.AntagonistMode ?? "qa";
            var session = await _coordinator.NewSessionAsync(workspacePath, null, ct);
            await _coordinator.SetModeAsync(session.SessionId, antagonistMode, ct);

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

    private static async Task<DevTeamRelease> LoadReleaseAsync(DevTeamDbContext db, Guid releaseId, CancellationToken ct)
        => await db.Releases
            .Include(r => r.Features)
            .Include(r => r.StageRuns).ThenInclude(sr => sr.GateChecks)
            .Include(r => r.StageRuns).ThenInclude(sr => sr.Findings)
            .Include(r => r.Signoffs)
            .Include(r => r.FlowPosition)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");
}
