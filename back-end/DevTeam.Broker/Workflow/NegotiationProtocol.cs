using System.Text;
using System.Text.RegularExpressions;

using DevTeam.Broker.Domain;

namespace DevTeam.Broker.Workflow;

/// <summary>What should happen after a push-back round.</summary>
public enum NegotiationAction
{
    /// <summary>There is room to try again: the target stage gets the open points.</summary>
    Continue,

    /// <summary>The receiver says the check/requirement is wrong — a human must decide.</summary>
    EscalateToUser,

    /// <summary>Stuck after the cap, or the receiver is blocked — the analyst re-scopes.</summary>
    EscalateToBa,
}

/// <summary>One point's parsed answer from a receiving stage's completion message.</summary>
public sealed record PointResponse(ReviewFinding Point, ReviewFindingResponse Kind, string Detail);

/// <summary>
/// The rules that stop two stages bouncing work back and forth forever. Every push-back carries
/// a round number and a numbered list of points; the receiving stage answers each point
/// (addressed / disputed / blocked); the pusher can only continue while there is room and no
/// signal to escalate. Pure and deterministic so it can be unit-tested without an engine.
/// </summary>
public static partial class NegotiationProtocol
{
    public const int MaxRounds = 3;

    public static int NextRound(IEnumerable<ReviewFinding> pointsAlreadySentToTarget)
    {
        var rounds = pointsAlreadySentToTarget.Select(p => p.Round).DefaultIfEmpty(0);
        return rounds.Max() + 1;
    }

    /// <summary>
    /// The stop rules, in priority order: a dispute goes to the human (the check may be wrong), a
    /// block goes to the analyst (the work can't be done as scoped), and exceeding the cap goes to
    /// the analyst for re-scoping. Otherwise there is still room to continue.
    /// </summary>
    public static NegotiationAction Decide(int round, IReadOnlyList<ReviewFinding> openPoints)
    {
        if (openPoints.Any(p => p.ResponseKind == ReviewFindingResponse.Disputed))
            return NegotiationAction.EscalateToUser;

        if (openPoints.Any(p => p.ResponseKind == ReviewFindingResponse.Blocked))
            return NegotiationAction.EscalateToBa;

        if (round > MaxRounds && openPoints.Count > 0)
            return NegotiationAction.EscalateToBa;

        return NegotiationAction.Continue;
    }

    /// <summary>The point-form block injected into the receiving stage's prompt.</summary>
    public static string BuildPointsBlock(IReadOnlyList<ReviewFinding> openPoints)
    {
        if (openPoints.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("Feedback from the team — answer every point below, point by point:");
        for (var i = 0; i < openPoints.Count; i++)
        {
            var point = openPoints[i];
            builder.Append(i + 1).Append(". ");
            if (!string.IsNullOrWhiteSpace(point.RequirementRef))
                builder.Append('[').Append(point.RequirementRef).Append("] ");
            builder.AppendLine(point.Summary);
            if (!string.IsNullOrWhiteSpace(point.Expected))
                builder.Append("   Expected: ").AppendLine(point.Expected);
        }

        builder.AppendLine();
        builder.AppendLine("For each numbered point, reply on its own line with exactly one of:");
        builder.AppendLine("  ADDRESSED — what you changed (name the files).");
        builder.AppendLine("  DISPUTED — why the point or check is wrong.");
        builder.AppendLine("  BLOCKED — what stops you from satisfying it.");
        builder.Append("If you cannot answer a point, say BLOCKED and why.");
        return builder.ToString();
    }

    /// <summary>
    /// Reads the receiving stage's completion message back into per-point answers. A point the
    /// message never mentions stays <see cref="ReviewFindingResponse.None"/>, which counts as
    /// unanswered and keeps the point open.
    /// </summary>
    public static IReadOnlyList<PointResponse> ParseResponses(string? responseText, IReadOnlyList<ReviewFinding> openPoints)
    {
        var responses = new List<PointResponse>(openPoints.Count);
        var text = responseText ?? string.Empty;

        for (var i = 0; i < openPoints.Count; i++)
        {
            var match = ResponseLine(i + 1).Match(text);
            if (!match.Success)
            {
                responses.Add(new PointResponse(openPoints[i], ReviewFindingResponse.None, string.Empty));
                continue;
            }

            var kind = match.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "ADDRESSED" => ReviewFindingResponse.Addressed,
                "DISPUTED" => ReviewFindingResponse.Disputed,
                "BLOCKED" => ReviewFindingResponse.Blocked,
                _ => ReviewFindingResponse.None,
            };
            responses.Add(new PointResponse(openPoints[i], kind, match.Groups["detail"].Value.Trim()));
        }

        return responses;
    }

    public static string ReScopeSummary(int round) =>
        $"The work has gone back and forth {round - 1} time(s) without converging. " +
        "Re-scope the requirements so the next attempt has something it can actually satisfy.";

    // Matches a line that answers point N: "1. ADDRESSED — did X", "#2: BLOCKED because Y",
    // "Point 3 — DISPUTED: ...". Deliberately line-anchored so prose mentioning a keyword
    // elsewhere doesn't get mistaken for an answer.
    private static Regex ResponseLine(int number) => new(
        $@"(?im)^\s*(?:#|point\s*)?{number}\s*[.):\-–—]*\s*(?<kind>ADDRESSED|DISPUTED|BLOCKED)\b[ \t:—\-–]*(?<detail>.*)$",
        RegexOptions.Compiled);
}
