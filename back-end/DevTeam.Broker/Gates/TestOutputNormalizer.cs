using System.Text.RegularExpressions;

namespace DevTeam.Broker.Gates;

public static class TestOutputNormalizer
{
    private static readonly Regex PassedSummaryRegex = new(
        @"Passed!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex JestSummaryRegex = new(
        @"Tests:\s+(?<failed>\d+)\s+failed,\s+(?<passed>\d+)\s+passed",
        RegexOptions.Compiled);

    private static readonly Regex FailureFileRegex = new(
        @"(?<file>(?:\.{1,2}[/\\]|/)?[\w.][\w.\-/\\]*\.(?:cs|ts|tsx|js|jsx))\s*(?:(?:\(|:)\s*line\s*(?<line1>\d+)|\((?<line2>\d+)|:(?<line3>\d+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WarningLineRegex = new(@"^\s*warning\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Normalize(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return "No test output captured.";

        var lines = rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var summary = ExtractSummary(lines);
        var failures = ExtractFailures(lines);

        var builder = new System.Text.StringBuilder();
        if (summary is not null)
            builder.Append(summary).Append('\n');
        foreach (var failure in failures)
            builder.Append("fail: ").Append(failure).Append('\n');
        if (summary is null && failures.Length == 0)
            builder.Append(lines.Length > 12 ? LinesTruncated(lines) : string.Join('\n', lines));

        return builder.ToString().TrimEnd();
    }

    public static int FailedCount(string normalizedOutput)
    {
        var match = NormalizedSummaryRegex.Match(normalizedOutput);
        return match.Success ? int.Parse(match.Groups["failed"].Value) : 0;
    }

    private static readonly Regex NormalizedSummaryRegex = new(
        @"tests:\s+(\d+)\s+passed,\s+(?<failed>\d+)\s+failed",
        RegexOptions.Compiled);

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

    private static string[] ExtractFailures(string[] lines)
    {
        var failures = new List<string>();
        foreach (var line in lines)
        {
            if (WarningLineRegex.IsMatch(line))
                continue;
            if (PassedSummaryRegex.IsMatch(line) || JestSummaryRegex.IsMatch(line))
                continue;

            var match = FailureFileRegex.Match(line);
            if (match.Success)
            {
                var failureLine = match.Groups["line1"].Success ? match.Groups["line1"].Value
                : match.Groups["line2"].Success ? match.Groups["line2"].Value
                : match.Groups["line3"].Value;
                failures.Add(TrimmedFailure($"{match.Groups["file"].Value}:{failureLine}"));
            }
            else if (line.StartsWith("Failed ", StringComparison.Ordinal) || line.StartsWith("Failed:", StringComparison.Ordinal))
                failures.Add(TrimmedFailure(line));
        }

        return failures.Take(10).ToArray();
    }

    private static string TrimmedFailure(string text)
    {
        var cleaned = text.Trim();
        return cleaned.Length > 140 ? cleaned[..137] + "…" : cleaned;
    }

    private static string LinesTruncated(string[] lines)
        => string.Join('\n', lines.Take(12)) + $"\n… {lines.Length - 12} more lines truncated";
}