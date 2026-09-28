using System.Text.RegularExpressions;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Turns raw `dotnet test` / `jest` output into one summary line plus one `fail:` line per
/// genuinely failed test. A failure is a *test entry* (dotnet's "Failed &lt;test&gt; [x ms]",
/// jest's bullet-headed test name) - never a bare file:line, never a compiler warning, never
/// a console warning, never a stack frame. Test runners print file:line references all over
/// their output (including inside node_modules and react-dom's own internals) for tests that
/// passed, so treating every file:line as a failure reports failures nobody can fix and
/// loops the developer stage against output that is already green.
/// </summary>
public static class TestOutputNormalizer
{
    private const int MaxFailures = 20;

    private static readonly Regex PassedSummaryRegex = new(
        @"Passed!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex JestSummaryRegex = new(
        @"Tests:\s+(?<failed>\d+)\s+failed,\s+(?<passed>\d+)\s+passed",
        RegexOptions.Compiled);

    private static readonly Regex NormalizedSummaryRegex = new(
        @"tests:\s+(\d+)\s+passed,\s+(?<failed>\d+)\s+failed",
        RegexOptions.Compiled);

    // dotnet: "  Failed Namespace.Type.Method [11 ms]" or "  Failed: Namespace.Type.Method".
    // The summary lines ("Failed!  - Failed: 0, Passed: 41, ...") can't match: they are
    // "Failed!" with no whitespace, and their captured name is a bare count.
    private static readonly Regex DotnetFailureRegex = new(
        @"^\s*Failed:?\s+(?<name>.+?)(?:\s*\[[^\]]*\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // jest/vitest: "  <bullet> Release wizard > saves settings". Bullet, bullet and the ASCII
    // stand-in all appear in the wild, so accept any of them.
    private static readonly Regex JestFailureRegex = new(
        @"^\s*(?:\u25CF|\u2022|\*)\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled);

    public static string Normalize(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return "No test output captured.";

        var lines = rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var summary = ExtractSummary(lines);
        var failures = FailureEntries(rawOutput);

        var builder = new System.Text.StringBuilder();
        if (summary is not null)
            builder.Append(summary).Append('\n');
        foreach (var failure in failures)
            builder.Append("fail: ").Append(failure).Append('\n');
        if (summary is null && failures.Count == 0)
            builder.Append(lines.Length > 12 ? LinesTruncated(lines) : string.Join('\n', lines));

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The failed test names in this run, in output order, deduplicated - the single
    /// authoritative failure list. Gate verdicts and the Test Runner report both read this
    /// rather than re-deriving "did something fail" from the rendered text.
    /// </summary>
    public static IReadOnlyList<string> FailureEntries(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return [];

        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in rawOutput.Split('\n', StringSplitOptions.TrimEntries))
        {
            var name = FailureName(line);
            if (name is null || !seen.Add(name))
                continue;
            failures.Add(name);
            if (failures.Count >= MaxFailures)
                break;
        }

        return failures;
    }

    public static int FailedCount(string normalizedOutput)
    {
        var match = NormalizedSummaryRegex.Match(normalizedOutput);
        return match.Success ? int.Parse(match.Groups["failed"].Value) : 0;
    }

    private static string? FailureName(string line)
    {
        var jest = JestFailureRegex.Match(line);
        if (jest.Success)
        {
            var name = Clean(jest.Groups["name"].Value);
            // A "Console" heading introduces jest's captured console output, not a failing test.
            return name.Length > 0 && !name.StartsWith("Console", StringComparison.OrdinalIgnoreCase)
                ? name
                : null;
        }

        var dotnet = DotnetFailureRegex.Match(line);
        if (!dotnet.Success)
            return null;

        var candidate = Clean(dotnet.Groups["name"].Value);
        // A bare count means this was a summary line ("Failed: 0, Passed: 41"), not a test name.
        return candidate.Length > 0 && !candidate.All(char.IsDigit) ? candidate : null;
    }

    private static string Clean(string name)
    {
        var cleaned = name.Trim().TrimEnd('.', ',', ';');
        return cleaned.Length > 160 ? cleaned[..157] + "..." : cleaned;
    }

    private static string? ExtractSummary(string[] lines)
    {
        foreach (var line in lines)
        {
            var match = PassedSummaryRegex.Match(line);
            if (match.Success)
                return $"tests: {match.Groups["passed"].Value} passed, {match.Groups["failed"].Value} failed";

            match = JestSummaryRegex.Match(line);
            if (match.Success)
                return $"tests: {match.Groups["passed"].Value} passed, {match.Groups["failed"].Value} failed";
        }

        return null;
    }

    private static string LinesTruncated(string[] lines)
        => string.Join('\n', lines.Take(12)) + $"\n... {lines.Length - 12} more lines truncated";
}
