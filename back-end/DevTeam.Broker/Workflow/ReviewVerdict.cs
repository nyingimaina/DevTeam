using System.Text.RegularExpressions;

namespace DevTeam.Broker.Workflow;

public enum ReviewOutcome
{
    /// <summary>The reviewer never committed to a verdict (truncated, rambling, or off-contract).</summary>
    Missing,
    Pass,
    Fail,
}

/// <summary>
/// What a reviewing agent (a GatePrompt step or a challenge antagonist) concluded. A review used to
/// pass whenever the model's turn merely ended normally, so its text - the actual findings - was
/// never read and a review could not fail on substance. Now the reviewer must end its reply with a
/// single machine-readable line, and anything else is "no verdict", which a person resolves.
/// </summary>
public sealed record ReviewVerdict(ReviewOutcome Outcome, string Detail)
{
    /// <summary>Appended to every review prompt. Also the marker tests use to recognise one.</summary>
    public const string Instruction =
        "Finish your reply with exactly one final line: `VERDICT: PASS` if you found nothing that must change, " +
        "or `VERDICT: FAIL - <one sentence naming what must change>`. Put your reasoning above that line. " +
        "A reply without that final line is discarded.";

    public const string CorrectionPrompt =
        "Your reply had no verdict line. Reply with only the final line: `VERDICT: PASS` or " +
        "`VERDICT: FAIL - <one sentence naming what must change>`.";

    private static readonly Regex VerdictLine = new(
        @"^[ \t>*`]*VERDICT[ \t*`]*:[ \t*`]*(?<kind>PASS|FAIL)\b[ \t*`]*[-–—:]?[ \t*`]*(?<detail>[^\r\n]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The LAST verdict line wins: a reviewer may quote the format earlier while reasoning.</summary>
    public static ReviewVerdict Parse(string? replyText)
    {
        if (string.IsNullOrWhiteSpace(replyText))
            return new ReviewVerdict(ReviewOutcome.Missing, string.Empty);

        Match? last = null;
        foreach (Match match in VerdictLine.Matches(replyText))
            last = match;

        if (last is null)
            return new ReviewVerdict(ReviewOutcome.Missing, string.Empty);

        var outcome = last.Groups["kind"].Value.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            ? ReviewOutcome.Pass
            : ReviewOutcome.Fail;
        return new ReviewVerdict(outcome, last.Groups["detail"].Value.Trim());
    }
}
