using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevTeam.Broker.Gates;

/// <summary>
/// The machine-extracted result of one full test run - written by the test_run gate and read by
/// the test-runner agent (to author its report) and by the test_report gate (to decide the
/// verdict). Keeping the machine data in a file is what makes the report an LLM's work over facts
/// rather than a second opinion about what failed.
/// </summary>
public sealed record TestRunRecord(
    string Command,
    int ExitCode,
    int FailedCount,
    IReadOnlyList<string> Failures,
    bool TimedOut,
    DateTimeOffset RanAt);

public static class TestRunIO
{
    public const string FileName = "test-run.json";

    public static string FilePath(string workspacePath, string featureKey)
        => Path.Combine(ArtifactPaths.FeatureDir(workspacePath, featureKey), FileName);

    public static void Write(string workspacePath, string featureKey, TestRunRecord record)
    {
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspacePath, featureKey));
        System.IO.File.WriteAllText(FilePath(workspacePath, featureKey), JsonSerializer.Serialize(record, Options));
    }

    public static TestRunRecord? TryRead(string workspacePath, string featureKey)
    {
        var path = FilePath(workspacePath, featureKey);
        if (!System.IO.File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<TestRunRecord>(System.IO.File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>One failure the report has to account for, and how it accounted for it.</summary>
public sealed record TestReportSection(
    string Number,
    string Heading,
    string Body,
    string Verdict,
    string? TestFile,
    string? Ruling)
{
    public bool IsChallenge => Verdict.Equals("challenge", StringComparison.OrdinalIgnoreCase);

    public bool IsFix => Verdict.Equals("fix", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses the LLM-authored test report. The rules are deliberately rigid - a report whose
/// sections can't be tied back to a machine-extracted failure is not evidence, it is prose.
/// </summary>
public static class TestReportReader
{
    private static readonly Regex SectionRegex = new(
        @"^##\s+TEST-(?<number>\d+)\s*:\s*(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex VerdictRegex = new(
        @"^[\s>*\-]*Verdict\s*:\s*(?<verdict>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex TestFileRegex = new(
        @"^[\s>*\-]*Test\s*:\s*(?<file>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex RulingRegex = new(
        @"^[\s>*\-]*Ruling\s*:\s*(?<ruling>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static IReadOnlyList<TestReportSection> Parse(string report)
    {
        if (string.IsNullOrWhiteSpace(report))
            return [];

        var matches = SectionRegex.Matches(report);
        var sections = new List<TestReportSection>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : report.Length;
            var body = report[start..end];
            sections.Add(new TestReportSection(
                Number: matches[i].Groups["number"].Value,
                Heading: matches[i].Groups["title"].Value,
                Body: body,
                Verdict: Group(VerdictRegex, body, "verdict") ?? string.Empty,
                TestFile: Group(TestFileRegex, body, "file"),
                Ruling: Group(RulingRegex, body, "ruling")));
        }

        return sections;
    }

    /// <summary>
    /// Whether this section accounts for the given machine-extracted failure. The names differ
    /// between runners (jest's "suite &gt; test" vs dotnet's Namespace.Type.Method), so this is a
    /// containment check on the section text rather than an equality check.
    /// </summary>
    public static bool AccountsFor(TestReportSection section, string failure)
    {
        var needle = Normalize(failure);
        if (needle.Length == 0)
            return true;
        return Normalize(section.Body).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Group(Regex regex, string body, string groupName)
    {
        var match = regex.Match(body);
        return match.Success ? match.Groups[groupName].Value : null;
    }

    private static string Normalize(string text)
        => text.Replace('\u203A', '>').Replace('\u25CF', '*').Trim();
}

/// <summary>
/// The sanctioned way a disputed requirement changes. The BRS is the contract, so it is never
/// rewritten: an approved challenge produces a separate addendum document, and the only edit to
/// the original BRS is a link to it (BrsMutation.AppendAddendumLink) so a reader of the BRS
/// cannot miss that a requirement was revised.
/// </summary>
public static class TestAddendum
{
    public const string Prefix = "BRS.addendum-";

    public static string FileName(int id) => $"{Prefix}{id}.md";

    public static string FilePath(string workspacePath, string featureKey, int id)
        => System.IO.Path.Combine(ArtifactPaths.FeatureDir(workspacePath, featureKey), FileName(id));

    public static void Write(string workspacePath, string featureKey, int id, string body)
    {
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspacePath, featureKey));
        System.IO.File.WriteAllText(FilePath(workspacePath, featureKey, id), body);
    }

    public static bool Exists(string workspacePath, string featureKey, int id)
        => System.IO.File.Exists(FilePath(workspacePath, featureKey, id));

    public static bool IsAccepted(string? ruling)
        => ruling is not null
            && ruling.Contains("accepted", StringComparison.OrdinalIgnoreCase)
            && !ruling.Contains("rejected", StringComparison.OrdinalIgnoreCase);

    public static bool IsRejected(string? ruling)
        => ruling is not null && ruling.Contains("rejected", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The one mutation the challenge protocol allows on the BRS: a single link line to an addendum.
/// Named as its own type so the "BRS is never rewritten" rule is a code-level invariant rather
/// than a prompt's plea.
/// </summary>
public static class BrsMutation
{
    private const string LinkPrefix = "- See addendum:";

    public static void AppendAddendumLink(string workspacePath, string featureKey, int id)
    {
        var path = ArtifactPaths.BrsPath(workspacePath, featureKey);
        var existing = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
        if (HasAddendumLink(existing, id))
            return;
        var line = $"{LinkPrefix} {TestAddendum.FileName(id)}";
        var updated = existing.Length == 0 || existing.EndsWith('\n') ? existing + line + "\n" : existing + "\n" + line + "\n";
        System.IO.File.WriteAllText(path, updated);
    }

    public static bool HasAddendumLink(string brsText, int id)
        => brsText.Contains($"{LinkPrefix} {TestAddendum.FileName(id)}", StringComparison.OrdinalIgnoreCase);
}
