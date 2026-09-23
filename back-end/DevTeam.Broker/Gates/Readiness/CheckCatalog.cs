namespace DevTeam.Broker.Gates.Readiness;

/// <summary>
/// One entry in the Checks library — the plain-language description of a check, for a person who
/// has never seen a command name or a stage id.
/// </summary>
public sealed record CheckDefinition(
    string Id,
    string Title,
    string Category,
    string WhyItMatters,
    string HowToFix,
    bool Required,
    string Scope,
    string Technical);

/// <summary>
/// The library of checks DevTeam knows about, described for non-technical readers. Copy is keyed
/// by phase id (see ReadinessProfileDetector) with a generic fallback, so a hand-authored
/// devteam/readiness.yaml still gets a readable card instead of a raw command.
/// </summary>
public static class CheckCatalog
{
    private const string Backend = "backend";
    private const string Frontend = "frontend";
    private const string Other = "general";

    private static readonly Dictionary<string, (string Category, string Why, string How)> Known = new()
    {
        ["backend-build"] = (Backend,
            "If the project doesn't build, nothing else about it can be trusted.",
            "Fix the build errors reported by the compiler."),
        ["backend-unit"] = (Backend,
            "The tests describe the behaviour we promised, in a form a machine can re-check.",
            "Make the failing tests pass — or correct the test if the expectation was wrong."),
        ["backend-integration"] = (Backend,
            "Unit tests can pass while the real database interaction is still broken.",
            "Start the database and fix the integration failures."),
        ["frontend-typecheck"] = (Frontend,
            "Type errors are mistakes the computer can see before a person ever runs the app.",
            "Resolve the type errors in the screens."),
        ["frontend-build"] = (Frontend,
            "A website that won't build can't be delivered to anyone.",
            "Fix whatever the build reports."),
        ["frontend-lint"] = (Frontend,
            "Consistent style keeps the codebase cheap to change later.",
            "Apply the house rules, or agree an exception."),
        ["frontend-tests"] = (Frontend,
            "The screen tests prove the user-facing behaviour still works.",
            "Make the failing screen tests pass."),
    };

    public static IReadOnlyList<CheckDefinition> Describe(IReadOnlyList<ReadinessPhaseDefinition> phases)
        => phases.Select(DescribeOne).ToList();

    private static CheckDefinition DescribeOne(ReadinessPhaseDefinition phase)
    {
        var (category, why, how) = Known.TryGetValue(phase.Id, out var known)
            ? known
            : (Other,
               "This check protects the quality of what ships.",
               "Look at the details for what went wrong, then fix it.");

        return new CheckDefinition(
            Id: phase.Id,
            Title: phase.Title,
            Category: category,
            WhyItMatters: why,
            HowToFix: how,
            Required: phase.Required,
            Scope: "project",
            Technical: phase.Command);
    }
}
