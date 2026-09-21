using System.Text;

using DevTeam.Broker.Domain;

namespace DevTeam.Broker.Workflow;

public enum GateHealOutcome
{
    /// <summary>The stage's checks passed (possibly after the agent fixed something on its own).</summary>
    Passed,

    /// <summary>Automatic fixing didn't resolve it; a person has to look.</summary>
    NeedsYou,
}

public sealed record GateHealResult(
    DevTeamRelease Release,
    GateHealOutcome Outcome,
    int AutoFixAttempts,
    IReadOnlyList<GateProblem> Problems);

/// <summary>
/// Wraps <see cref="IWorkflowEngine.RunGatesAsync"/> so a failed check isn't a dead end: the agent
/// that produced the work is told what's wrong (in words it can act on), asked to fix it, and the
/// checks re-run — up to <c>maxAutoFixes</c> times before the problem is handed to the user.
/// </summary>
public sealed class GateSelfHealer(IWorkflowEngine engine, int maxAutoFixes = 2)
{
    public async Task<GateHealResult> RunAsync(Guid featureId, CancellationToken ct)
    {
        var release = await engine.RunGatesAsync(featureId, ct);
        var fixes = 0;

        while (true)
        {
            var problems = ProblemsFor(release, featureId);
            if (problems.Count == 0)
                return new GateHealResult(release, GateHealOutcome.Passed, fixes, []);

            if (fixes >= maxAutoFixes)
                return new GateHealResult(release, GateHealOutcome.NeedsYou, fixes, problems);

            try
            {
                await engine.ReopenBlockedGateAsync(featureId, ct);
                await engine.SendMessageAsync(featureId, BuildFixRequest(problems), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var latest = await engine.GetReleaseAsync(release.Id, ct);
                var unreachable = new GateProblem(
                    "agent", "The agent couldn't be reached",
                    "The agent didn't respond when asked to fix the problem above.", ex.Message);
                return new GateHealResult(latest, GateHealOutcome.NeedsYou, fixes, [.. problems, unreachable]);
            }

            fixes++;
            release = await engine.RunGatesAsync(featureId, ct);
        }
    }

    private static List<GateProblem> ProblemsFor(DevTeamRelease release, Guid featureId)
    {
        var feature = release.Features.FirstOrDefault(f => f.Id == featureId);
        var blocked = feature?.StageRuns
            .Where(sr => sr.Status == ReleaseStageStatus.BlockedGate)
            .OrderByDescending(sr => sr.Attempt)
            .FirstOrDefault();
        if (blocked is null) return [];

        var problems = blocked.GateChecks
            .Where(gc => !gc.Passed && !gc.IsEntryGate)
            .Select(gc => GateFriendlyText.Describe(gc.Name, gc.EvidenceText))
            .ToList();

        // Blocked with every check green means the reviewer step (not a check) rejected the work.
        if (problems.Count == 0)
            problems.Add(new GateProblem(
                "review", "The review found issues",
                "A review of the work found problems that need fixing.", blocked.Summary ?? string.Empty));

        return problems;
    }

    private static string BuildFixRequest(IReadOnlyList<GateProblem> problems)
    {
        var text = new StringBuilder("The automatic checks on your work found problems. Please fix them yourself now:\n");
        foreach (var p in problems)
        {
            text.Append("\n- ").Append(p.Title).Append(": ").Append(p.WhatWentWrong);
            if (p.TechnicalDetail.Length > 0)
                text.Append("\n  Details from the check:\n  ").Append(p.TechnicalDetail.Replace("\n", "\n  "));
        }

        text.Append("\n\nEdit the files to resolve every item, then reply with DONE on its own line when finished. ")
            .Append("Don't ask the user anything for this — just make the fix.");
        return text.ToString();
    }
}
