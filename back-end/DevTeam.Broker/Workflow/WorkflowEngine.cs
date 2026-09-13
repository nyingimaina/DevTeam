using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Workflow;

public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly IGateRunner _gateRunner;
    private readonly IAgentSpoke _agentSpoke;
    private readonly IEventBroadcaster _broadcaster;
    private readonly WorkflowDefinitionLoader _loader;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IDbContextFactory<DevTeamDbContext> dbFactory,
        IGateRunner gateRunner,
        IAgentSpoke agentSpoke,
        IEventBroadcaster broadcaster,
        WorkflowDefinitionLoader loader,
        ILogger<WorkflowEngine> logger)
    {
        _dbFactory = dbFactory;
        _gateRunner = gateRunner;
        _agentSpoke = agentSpoke;
        _broadcaster = broadcaster;
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
        _logger.LogInformation("Started release {ReleaseId} for feature {FeatureKey}", release.Id, featureKey);
        return release;
    }

    public async Task<DevTeamRelease> AdvanceAsync(Guid releaseId, CancellationToken ct)
    {
        var workflow = LoadWorkflow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var release = await LoadReleaseAsync(db, releaseId, ct);
        var position = release.FlowPosition
            ?? throw new InvalidOperationException("Release has no flow position.");

        var currentStageIndex = position.CurrentStageIndex;
        if (currentStageIndex >= workflow.Pipeline.Count)
        {
            release.Status = ReleaseStatus.Ready;
            release.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return release;
        }

        var role = workflow.Pipeline[currentStageIndex];
        var featureKey = release.Features.FirstOrDefault()?.Key
            ?? throw new InvalidOperationException("Release has no feature.");

        var stageRun = new ReleaseStageRun
        {
            Release = release,
            StageName = role.Name,
            Status = ReleaseStageStatus.Active,
        };
        db.ReleaseStageRuns.Add(stageRun);
        await db.SaveChangesAsync(ct);

        var allPassed = true;
        foreach (var step in role.Steps)
        {
            var result = await ExecuteStepAsync(step, featureKey, release.WorkspacePath, ct);
            db.ReleaseGateChecks.Add(new ReleaseGateCheck
            {
                StageRun = stageRun,
                Name = result.StepName,
                Passed = result.Passed,
                EvidenceText = result.Evidence,
            });

            if (!result.Passed)
                allPassed = false;
        }

        stageRun.Status = allPassed ? ReleaseStageStatus.Complete : ReleaseStageStatus.BlockedGate;
        stageRun.FinishedAt = DateTimeOffset.UtcNow;
        stageRun.Summary = allPassed
            ? $"Stage {role.Name} passed all gates."
            : $"Stage {role.Name} failed gates.";

        if (allPassed && role.Signoff is not null)
        {
            var needsSignoff = release.Signoffs.Any(s => s.StageName == role.Name && !s.Approved);
            if (needsSignoff)
            {
                stageRun.Status = ReleaseStageStatus.BlockedSignoff;
                release.Status = ReleaseStatus.Blocked;
                release.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return release;
            }
        }

        if (allPassed && role.Signoff is null)
        {
            position.CurrentStageIndex++;
            position.CurrentStageName = currentStageIndex + 1 < workflow.Pipeline.Count
                ? workflow.Pipeline[currentStageIndex + 1].Name
                : "done";
            position.UpdatedAt = DateTimeOffset.UtcNow;

            if (position.CurrentStageIndex >= workflow.Pipeline.Count)
            {
                release.Status = ReleaseStatus.Ready;
                release.Features.ForEach(f => f.Status = ReleaseFeatureStatus.Complete);
            }
        }

        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastEventAsync(release.Id, "stageStateChanged", new
        {
            StageName = role.Name,
            Status = stageRun.Status.ToString(),
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

    private async Task<StepExecutionResult> ExecuteStepAsync(WorkflowStep step, string featureKey, string workspacePath, CancellationToken ct) =>
        step.Kind switch
        {
            WorkflowStepKind.Builtin => await ExecuteBuiltinAsync(step.Builtin!, featureKey, workspacePath, ct),
            WorkflowStepKind.Agent => await ExecuteAgentAsync(step.AgentMode!, featureKey, workspacePath, ct),
            WorkflowStepKind.Loop => await ExecuteLoopAsync(step, featureKey, workspacePath, ct),
            _ => throw new InvalidOperationException($"Unknown step kind: {step.Kind}"),
        };

    private async Task<StepExecutionResult> ExecuteBuiltinAsync(string gateName, string featureKey, string workspacePath, CancellationToken ct)
    {
        var request = new GateRequest(gateName, workspacePath, featureKey);
        var result = await _gateRunner.RunAsync(gateName, request, ct);
        return new StepExecutionResult { StepName = gateName, Passed = result.Passed, Evidence = result.EvidenceText };
    }

    private async Task<StepExecutionResult> ExecuteAgentAsync(string mode, string featureKey, string workspacePath, CancellationToken ct)
    {
        var session = await _agentSpoke.NewSessionAsync(workspacePath, ct);
        var result = await _agentSpoke.PromptAsync(session.SessionId, [new AgentPromptPart("text", $"Work on feature {featureKey}.")], ct);
        return new StepExecutionResult { StepName = $"agent:{mode}", Passed = result.StopReason == "end_turn", Evidence = "Agent completed." };
    }

    private async Task<StepExecutionResult> ExecuteLoopAsync(WorkflowStep step, string featureKey, string workspacePath, CancellationToken ct)
    {
        var maxAttempts = step.LoopAttempts ?? 3;
        var lastResult = new StepExecutionResult { StepName = "loop", Passed = false };
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            foreach (var innerStep in step.LoopSteps ?? [])
            {
                lastResult = await ExecuteStepAsync(innerStep, featureKey, workspacePath, ct);
                if (!lastResult.Passed) break;
            }
            if (lastResult.Passed) break;
        }
        return lastResult with { StepName = $"loop({maxAttempts})" };
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
            .Include(r => r.Signoffs)
            .Include(r => r.FlowPosition)
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");
}
