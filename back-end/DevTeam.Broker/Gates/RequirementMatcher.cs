using System.Text.RegularExpressions;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Decides whether a body of text (test names + test file contents, or source files) refers to a
/// requirement id. Tolerant of the spellings a developer actually uses — REQ-001 / REQ001 /
/// REQ_001 / REQ 001 / REQ-1 — while keeping REQ-1 from being "matched" by REQ-10. One
/// implementation so the coverage gate and the progress meter can never disagree about coverage.
/// </summary>
public static partial class RequirementMatcher
{
    // An id like "REQ-001" is <prefix><separator><number>.
    private static readonly Regex IdShape = new(
        @"^(?<prefix>[A-Za-z]+)[-_\s]*(?<num>\d+)$", RegexOptions.Compiled);

    [GeneratedRegex(@"\d", RegexOptions.Compiled)]
    private static partial Regex HasDigit();

    public static bool MatchesId(string corpus, string requirementId)
    {
        if (string.IsNullOrWhiteSpace(requirementId))
            return false;

        // Numeric ids go through the boundary-aware matcher; non-numeric/custom ids keep plain
        // substring matching (there is no number to bound against).
        return MatchesNormalizedId(corpus, requirementId)
            || (!HasDigit().IsMatch(requirementId) && Contains(corpus, requirementId));
    }

    // Id-based only. A requirement's *title* is deliberately NOT matched: "Add" would match any
    // test file that happens to contain the word, which is how a feature with no tests read as
    // fully covered. Coverage means a test names the requirement.
    public static bool MatchesRequirement(string corpus, RequirementDtos.Requirement requirement)
        => MatchesId(corpus, requirement.Id);

    public static bool Contains(string corpus, string token)
        => !string.IsNullOrWhiteSpace(token) && corpus.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>The number inside an id, unpadded (REQ-001 → "1"); used for example wording.</summary>
    public static string FirstNumber(string id)
    {
        var match = Regex.Match(id, @"\d+");
        if (!match.Success)
            return "1";

        var trimmed = match.Value.TrimStart('0');
        return trimmed.Length > 0 ? trimmed : "0";
    }

    private static bool MatchesNormalizedId(string corpus, string id)
    {
        var match = IdShape.Match(id.Trim());
        if (!match.Success || !long.TryParse(match.Groups["num"].Value, out var number))
            return false;

        // (?<!…) / (?!\d) keep REQ-1 from matching inside REQ-10.
        var pattern = $"(?<![A-Za-z]){Regex.Escape(match.Groups["prefix"].Value)}[-_\\s]*0*{number}(?!\\d)";
        return Regex.IsMatch(corpus, pattern, RegexOptions.IgnoreCase);
    }
}
