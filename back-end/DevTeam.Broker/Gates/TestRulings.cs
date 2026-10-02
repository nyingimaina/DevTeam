using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevTeam.Broker.Gates;

public enum RulingDecision
{
    /// <summary>The test is wrong: the requirement changes by addendum, and the test may follow it.</summary>
    Accept,

    /// <summary>The requirement stands: the code must be made to satisfy it as written.</summary>
    Reject,
}

/// <summary>A challenge the test-runner filed that only the operator can settle, in plain fields.</summary>
public sealed record PendingRuling(string Test, string Requirement, string Expected, string Observed, string Details);

public sealed record RulingResult(bool Ok, string? Problem, int? AddendumId);

/// <summary>
/// The operator's rulings on test challenges, kept by the broker rather than by the test-runner
/// agent. They used to exist only as a line the agent was asked to write into its own report, which
/// meant a novice had to instruct an LLM to record a decision, and the next turn that rewrote the
/// report could lose it. Stored beside the report and keyed by test, a ruling is one click, is
/// deterministic, and holds however many times the report is regenerated.
/// </summary>
public static class TestRulings
{
    public const string FileName = "test-rulings.json";

    private sealed record StoredRuling(string Test, string Decision, int? AddendumId, DateTimeOffset DecidedAt);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static readonly string[] StructuredFields =
        ["Test", "Requirement", "Verdict", "Expected", "Observed", "Ruling", "Classification", "Likely cause"];

    public static string FilePath(string workspacePath, string featureKey)
        => Path.Combine(ArtifactPaths.FeatureDir(workspacePath, featureKey), FileName);

    /// <summary>The ruling that governs a challenge section: the operator's recorded one, else what the report says.</summary>
    public static string? EffectiveRuling(string workspacePath, string featureKey, TestReportSection section)
    {
        var stored = Load(workspacePath, featureKey)
            .LastOrDefault(r => Key(r.Test) == Key(section.Heading));
        if (stored is null)
            return section.Ruling;

        return stored.Decision == nameof(RulingDecision.Accept) && stored.AddendumId is { } id
            ? $"accepted (addendum {TestAddendum.FileName(id)})"
            : "rejected - implement as written";
    }

    public static IReadOnlyList<PendingRuling> Pending(string workspacePath, string featureKey)
    {
        var report = ArtifactPaths.TestReportPath(workspacePath, featureKey);
        if (!File.Exists(report))
            return [];

        return TestReportReader.Parse(File.ReadAllText(report))
            .Where(section => section.IsChallenge && IsPending(EffectiveRuling(workspacePath, featureKey, section)))
            .Select(ToPending)
            .ToArray();
    }

    public static RulingResult Record(string workspacePath, string featureKey, string test, RulingDecision decision)
    {
        var report = ArtifactPaths.TestReportPath(workspacePath, featureKey);
        var section = File.Exists(report)
            ? TestReportReader.Parse(File.ReadAllText(report))
                .FirstOrDefault(s => s.IsChallenge && Key(s.Heading) == Key(test)
                    && IsPending(EffectiveRuling(workspacePath, featureKey, s)))
            : null;
        if (section is null)
            return new RulingResult(false, "That test is not waiting for a ruling.", null);

        int? addendumId = null;
        if (decision == RulingDecision.Accept)
        {
            addendumId = NextAddendumId(workspacePath, featureKey);
            TestAddendum.Write(workspacePath, featureKey, addendumId.Value, AddendumText(ToPending(section)));
            BrsMutation.AppendAddendumLink(workspacePath, featureKey, addendumId.Value);
        }

        var all = Load(workspacePath, featureKey).ToList();
        all.Add(new StoredRuling(section.Heading, decision.ToString(), addendumId, DateTimeOffset.UtcNow));
        File.WriteAllText(FilePath(workspacePath, featureKey), JsonSerializer.Serialize(all, Options));
        return new RulingResult(true, null, addendumId);
    }

    private static bool IsPending(string? ruling)
        => ruling is null || ruling.Contains("pending", StringComparison.OrdinalIgnoreCase);

    private static PendingRuling ToPending(TestReportSection section) => new(
        section.Heading,
        Field(section.Body, "Requirement") ?? string.Empty,
        Field(section.Body, "Expected") ?? string.Empty,
        Field(section.Body, "Observed") ?? string.Empty,
        Details(section.Body));

    // Everything the agent wrote beyond the structured lines: what the test asserts, why that is
    // wrong, and the change it proposes. This is what the operator is being asked to approve.
    private static string Details(string body)
    {
        var lines = body.Split('\n')
            .Skip(1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Where(line => !StructuredFields.Any(field =>
                Regex.IsMatch(line, $@"^[\s>*\-]*{Regex.Escape(field)}\s*:", RegexOptions.IgnoreCase)));
        return string.Join('\n', lines).Trim();
    }

    private static string? Field(string body, string name)
    {
        var match = Regex.Match(body, $@"^[\s>*\-]*{Regex.Escape(name)}\s*:\s*(?<value>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string AddendumText(PendingRuling ruling)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## {(ruling.Requirement.Length > 0 ? ruling.Requirement : "Requirement")} (revised - approved by the operator)");
        builder.AppendLine();
        builder.AppendLine($"Test in question: {ruling.Test}");
        if (ruling.Expected.Length > 0) builder.AppendLine($"The requirement as originally written: {ruling.Expected}");
        if (ruling.Observed.Length > 0) builder.AppendLine($"What the test checker found: {ruling.Observed}");
        if (ruling.Details.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Approved change, as proposed by the test checker:");
            builder.AppendLine(ruling.Details);
        }

        return builder.ToString();
    }

    private static int NextAddendumId(string workspacePath, string featureKey)
    {
        var directory = ArtifactPaths.FeatureDir(workspacePath, featureKey);
        var used = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, $"{TestAddendum.Prefix}*.md")
                .Select(file => Regex.Match(Path.GetFileName(file), @"(\d+)\.md$", RegexOptions.IgnoreCase))
                .Where(match => match.Success)
                .Select(match => int.Parse(match.Groups[1].Value))
            : [];
        var stored = Load(workspacePath, featureKey).Where(r => r.AddendumId is not null).Select(r => r.AddendumId!.Value);
        return used.Concat(stored).DefaultIfEmpty(0).Max() + 1;
    }

    private static IReadOnlyList<StoredRuling> Load(string workspacePath, string featureKey)
    {
        var path = FilePath(workspacePath, featureKey);
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<StoredRuling>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Key(string test) => test.Replace('›', '>').Trim().ToLowerInvariant();
}
