using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevTeam.Broker.Gates.Readiness;

/// <summary>The ordered set of strict checks a workspace must pass.</summary>
public sealed record ReadinessProfile(IReadOnlyList<ReadinessPhaseDefinition> Phases);

/// <summary>
/// Chooses which checks apply to a workspace. A hand-authored override at
/// <see cref="ReadinessProfileLoader.OverridePath"/> wins; otherwise the project's own shape
/// is detected (a .slnx/.sln → backend checks, a package.json → frontend checks) so no project
/// path is ever hard-coded into the gate.
/// </summary>
public static class ReadinessProfileLoader
{
    public const string OverridePath = "devteam/readiness.yaml";

    public static ReadinessProfile Load(string workspacePath)
    {
        var overridePath = Path.Combine(workspacePath, "devteam", "readiness.yaml");
        return File.Exists(overridePath)
            ? FromYaml(File.ReadAllText(overridePath))
            : ReadinessProfileDetector.Detect(workspacePath);
    }

    public static ReadinessProfile FromYaml(string yaml)
    {
        var parsed = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Deserialize<ReadinessYaml>(yaml);

        var phases = (parsed?.Phases ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Command))
            .Select(p => new ReadinessPhaseDefinition(
                p.Id!,
                string.IsNullOrWhiteSpace(p.Title) ? p.Id! : p.Title!,
                p.Command!,
                string.IsNullOrWhiteSpace(p.WorkingDirectory) ? null : p.WorkingDirectory,
                p.DependsOn ?? [],
                p.TimeoutMs > 0 ? p.TimeoutMs : ReadinessDefaults.PhaseTimeoutMs,
                p.Required,
                string.IsNullOrWhiteSpace(p.SkipWhen) ? null : p.SkipWhen,
                ConvertCoverage(p.Coverage)))
            .ToList();

        return new ReadinessProfile(phases);
    }

    private static ReadinessCoverageSpec? ConvertCoverage(ReadinessCoverageYaml? coverage)
        => coverage is null || string.IsNullOrWhiteSpace(coverage.RelativeJsonPath)
            ? null
            : new ReadinessCoverageSpec(coverage.RelativeJsonPath, coverage.LineMin, coverage.BranchMin, coverage.FunctionMin);
}

/// <summary>Wire shape of devteam/readiness.yaml — see <see cref="ReadinessProfileLoader"/>.</summary>
public sealed class ReadinessYaml
{
    public List<ReadinessPhaseYaml>? Phases { get; set; }
}

public sealed class ReadinessPhaseYaml
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Command { get; set; }
    public string? WorkingDirectory { get; set; }
    public List<string>? DependsOn { get; set; }
    public int TimeoutMs { get; set; }
    public bool Required { get; set; } = true;
    public string? SkipWhen { get; set; }
    public ReadinessCoverageYaml? Coverage { get; set; }
}

public sealed class ReadinessCoverageYaml
{
    public string? RelativeJsonPath { get; set; }
    public double? LineMin { get; set; }
    public double? BranchMin { get; set; }
    public double? FunctionMin { get; set; }
}

public static class ReadinessProfileDetector
{
    private const string BackendBuild = "backend-build";
    private const string BackendUnit = "backend-unit";
    private const string BackendIntegration = "backend-integration";
    private const string FrontendTypecheck = "frontend-typecheck";
    private const string FrontendBuild = "frontend-build";
    private const string FrontendLint = "frontend-lint";
    private const string FrontendTests = "frontend-tests";

    public static ReadinessProfile Detect(string workspacePath)
    {
        var phases = new List<ReadinessPhaseDefinition>();

        var solution = FindFirst(workspacePath, "*.slnx") ?? FindFirst(workspacePath, "*.sln");
        if (solution is not null)
            AddBackend(phases, Path.GetFileName(solution));

        var (hasFrontend, frontendDirectory) = FindFrontendDirectory(workspacePath);
        if (hasFrontend)
            AddFrontend(phases, frontendDirectory);

        return new ReadinessProfile(phases);
    }

    // Plain-language titles, because these become the cards a non-technical person reads — the
    // technical command stays an implementation detail. Ordered so a cheap, decisive check
    // (does it compile) gates the slower ones.
    private static void AddBackend(List<ReadinessPhaseDefinition> phases, string solution)
    {
        phases.Add(new ReadinessPhaseDefinition(
            BackendBuild, "The backend project builds", $"dotnet build \"{solution}\"", TimeoutMs: 15 * 60 * 1000));
        phases.Add(new ReadinessPhaseDefinition(
            BackendUnit, "The backend tests pass", $"dotnet test \"{solution}\" --filter \"Category!=Integration\"",
            DependsOn: [BackendBuild], TimeoutMs: 20 * 60 * 1000));
        phases.Add(new ReadinessPhaseDefinition(
            BackendIntegration, "The backend works against a real database",
            $"dotnet test \"{solution}\" --filter \"Category=Integration\"",
            DependsOn: [BackendUnit], TimeoutMs: 20 * 60 * 1000,
            SkipWhen: ReadinessDefaults.SkipWhenDatabaseUnreachable));
    }

    private static void AddFrontend(List<ReadinessPhaseDefinition> phases, string? frontendDirectory)
    {
        phases.Add(new ReadinessPhaseDefinition(
            FrontendTypecheck, "The screens have no type errors", "npx tsc --noEmit",
            WorkingDirectory: frontendDirectory, TimeoutMs: 10 * 60 * 1000));
        phases.Add(new ReadinessPhaseDefinition(
            FrontendBuild, "The website builds", "npx next build",
            WorkingDirectory: frontendDirectory, DependsOn: [FrontendTypecheck], TimeoutMs: 20 * 60 * 1000));
        phases.Add(new ReadinessPhaseDefinition(
            FrontendLint, "The code follows the project's house rules", "npx next lint",
            WorkingDirectory: frontendDirectory, TimeoutMs: 10 * 60 * 1000));
        phases.Add(new ReadinessPhaseDefinition(
            FrontendTests, "The screen tests pass", "npx jest --coverage",
            WorkingDirectory: frontendDirectory, TimeoutMs: 20 * 60 * 1000,
            Coverage: new ReadinessCoverageSpec("coverage/coverage-summary.json")));
    }

    private static string? FindFirst(string workspacePath, string pattern)
        => Directory.Exists(workspacePath)
            ? Directory.EnumerateFiles(workspacePath, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;

    // The two conventions in this codebase are "front-end" (DevTeam itself) and "frontend"
    // (the generated app's default); a package.json at the workspace root also counts, in which
    // case the working directory is the root itself (returned as null).
    private static (bool Found, string? Directory) FindFrontendDirectory(string workspacePath)
    {
        foreach (var candidate in new[] { "front-end", "frontend" })
        {
            if (File.Exists(Path.Combine(workspacePath, candidate, "package.json")))
                return (true, candidate);
        }

        return (File.Exists(Path.Combine(workspacePath, "package.json")), null);
    }
}
