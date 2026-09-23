using System.Text.RegularExpressions;

namespace DevTeam.Broker.Workflow;

/// <summary>A failed check, described for a person who has never seen the internal check names.</summary>
public sealed record GateProblem(string GateName, string Title, string WhatWentWrong, string TechnicalDetail);

/// <summary>
/// Display layer for gate checks: internal identifiers (gherkin_validator, BlockedGate, …) stay
/// stable for workflow files, stored data and tests, while everything a user reads goes through here.
/// </summary>
public static partial class GateFriendlyText
{
    private static readonly Dictionary<string, (string Title, string Meaning)> Known = new()
    {
        [BuiltinRegistry.ScaffoldSpecs] = ("Setting up the requirements documents",
            "The starting documents for this feature couldn't be created."),
        [BuiltinRegistry.CoreScaffold] = ("Preparing the shared project",
            "The single shared project every feature builds on couldn't be set up."),
        [BuiltinRegistry.RepoHygiene] = ("Tidying the project",
            "The project's file housekeeping (ignoring build output, logs and temp files) couldn't be completed."),
        [BuiltinRegistry.CodeMap] = ("Reading the project overview",
            "The short overview of the project couldn't be handed to the agent."),
        [BuiltinRegistry.ContextBundle] = ("Gathering background for the agent",
            "The background notes the agent works from couldn't be put together."),
        [BuiltinRegistry.GherkinValidator] = ("Every requirement has a clear example",
            "Each requirement needs an example that says the starting situation, what the person does, and what should happen."),
        [BuiltinRegistry.BuildCheck] = ("The project compiles",
            "The project didn't build."),
        [BuiltinRegistry.VerifyCode] = ("The code builds and its tests pass",
            "The project didn't build, or one of its tests failed."),
        [BuiltinRegistry.CodeHygiene] = ("The code is tidy",
            "Some of the code doesn't meet the project's tidiness rules."),
        [BuiltinRegistry.AppLaunch] = ("The app actually starts",
            "The app crashed or wouldn't start when it was launched."),
        [BuiltinRegistry.SliceGuard] = ("Work stays inside the agreed area",
            "Changes were made outside the area this feature is allowed to touch."),
        [BuiltinRegistry.SliceScope] = ("Only the expected files were changed",
            "Files were changed that this feature wasn't supposed to touch."),
        [BuiltinRegistry.ReuseGate] = ("Existing shared code is reused",
            "Something was written again that the project already has."),
        [BuiltinRegistry.ProjectStructure] = ("Features extend the one shared app",
            "A feature added its own project instead of extending the shared one."),
        [BuiltinRegistry.RenderPr] = ("Writing the change summary",
            "The summary describing this change couldn't be written."),
        [BuiltinRegistry.RenderHandoff] = ("Writing the notes for the next step",
            "The handoff notes for the next role couldn't be written."),
        [BuiltinRegistry.CoverageMatrix] = ("Every requirement is covered by a test",
            "At least one requirement has no test proving it works."),
        [BuiltinRegistry.FinalChecks] = ("Every final check passes",
            "At least one of the strict checks — the project builds, its tests pass, or the code follows the house rules — didn't pass."),
    };

    public static GateProblem Describe(string gateName, string? evidence)
    {
        var detail = evidence?.Trim() ?? string.Empty;

        if (!Known.TryGetValue(gateName, out var known))
        {
            var readable = gateName.Replace('_', ' ').Trim();
            var title = readable.Length == 0
                ? "A check"
                : char.ToUpperInvariant(readable[0]) + readable[1..];
            return new GateProblem(gateName, title, $"“{title}” didn't pass.", detail);
        }

        var whatWentWrong = gateName == BuiltinRegistry.GherkinValidator
            ? DescribeRequirementFailures(detail) ?? known.Meaning
            : known.Meaning;

        return new GateProblem(gateName, known.Title, whatWentWrong, detail);
    }

    // "fail: REQ-004: acceptance criteria missing Given, When" → one plain sentence per requirement.
    private static string? DescribeRequirementFailures(string evidence)
    {
        var sentences = new List<string>();
        foreach (Match match in FailLine().Matches(evidence))
        {
            var number = int.Parse(match.Groups["num"].Value);
            var missing = match.Groups["kw"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(PlainKeyword)
                .ToArray();
            sentences.Add($"Requirement {number} doesn't describe {JoinWithAnd(missing)}.");
        }

        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }

    private static string PlainKeyword(string keyword) => keyword.ToLowerInvariant() switch
    {
        "given" => "the starting situation",
        "when" => "what the person does",
        "then" => "what should happen as a result",
        _ => keyword,
    };

    private static string JoinWithAnd(string[] items) => items.Length switch
    {
        0 => "everything it should",
        1 => items[0],
        _ => string.Join(", ", items[..^1]) + " or " + items[^1],
    };

    [GeneratedRegex(@"^fail:\s*REQ-(?<num>\d+):\s*acceptance criteria missing\s+(?<kw>.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FailLine();
}
