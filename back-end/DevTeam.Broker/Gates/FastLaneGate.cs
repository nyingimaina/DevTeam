using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// The cheap "did what I just changed still hold together" check, run inside the developer loop
/// instead of the full app-wide build. It looks at the files this feature actually changed and
/// verifies only those:
/// <list type="bullet">
/// <item>C#: the solution build, which MSBuild already does incrementally - warm, it recompiles
/// only the stale projects, so it is seconds, not minutes.</item>
/// <item>TypeScript: <c>tsc --noEmit --incremental</c> (a cached whole-project type check, which
/// is the only still-correct way to type check - module edges cross file boundaries) followed by
/// <c>jest --changedSince=&lt;base&gt;</c>, which runs only the test files related to the
/// changed sources.</item>
/// </list>
/// It is triage, never a verdict: it stops a broken edit in seconds with a crisp error instead of
/// waiting for a full-suite round, but the authoritative full run still happens in the
/// test-runner stage. Failing here means "fix this now"; passing here means "not obviously
/// broken", nothing more.
/// </summary>
public sealed class FastLaneGate : IGate
{
    private const int StepTimeoutMs = 180_000;
    private const int MaxEvidenceChars = 4000;

    private readonly IProcessRunner _runner;

    public FastLaneGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.FastLane;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var workspace = request.WorkspacePath;
        var baseRef = GateInputs.GetOptional(request.Inputs, "baseRef");
        var changed = await ChangedPathsAsync(workspace, baseRef, cancellationToken);

        if (changed.Count == 0)
            return GateResult.Pass("Nothing changed to check", "fast lane: no changed files since " + (baseRef ?? "HEAD"));

        var scope = baseRef is null ? "HEAD" : baseRef;
        var evidence = new System.Text.StringBuilder();
        evidence.Append("fast lane: ").Append(changed.Count).Append(" changed file(s) since ").Append(scope).Append('\n');
        foreach (var path in changed.Take(20))
            evidence.Append("  ").Append(path).Append('\n');
        if (changed.Count > 20)
            evidence.Append("  ... ").Append(changed.Count - 20).Append(" more\n");

        if (changed.Any(ChangedFiles.IsDotNet))
        {
            var build = await new BuildCheckGate(_runner).RunAsync(
                request with { Builtin = BuiltinRegistry.BuildCheck },
                cancellationToken);
            if (!build.Passed)
                return GateResult.Fail("C# build failed for the changed files", evidence + "\n" + Tail(build.EvidenceText));
            evidence.Append("ok  dotnet build (incremental)\n");
        }

        if (changed.Any(ChangedFiles.IsTypeScript))
        {
            var frontendRoot = ChangedFiles.FrontendRoot(workspace, changed);
            if (frontendRoot is null)
            {
                evidence.Append("skip TypeScript: no package.json above the changed files\n");
            }
            else
            {
                if (File.Exists(Path.Combine(frontendRoot, "tsconfig.json")))
                {
                    var typeCheck = await StepAsync("npx tsc --noEmit --incremental", frontendRoot, cancellationToken);
                    if (!typeCheck.Passed)
                        return GateResult.Fail("Type check failed for the changed files", evidence + "\n" + typeCheck.Evidence);
                    evidence.Append("ok  tsc --noEmit --incremental\n");
                }

                var jest = await StepAsync(JestCommand(baseRef), frontendRoot, cancellationToken);
                if (!jest.Passed)
                    return GateResult.Fail("Tests related to the changed files failed", evidence + "\n" + jest.Evidence);
                evidence.Append("ok  jest (tests related to changed files)\n");
            }
        }

        return GateResult.Pass("Changed files pass the fast checks", evidence.ToString());
    }

    private static string JestCommand(string? baseRef)
        => baseRef is null
            ? "npx jest --onlyChanged --passWithNoTests"
            : $"npx jest --changedSince={baseRef} --passWithNoTests";

    private async Task<IReadOnlyList<string>> ChangedPathsAsync(
        string workspacePath, string? baseRef, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(IEnumerable<string> discovered)
        {
            foreach (var path in discovered)
            {
                if (seen.Add(path))
                    paths.Add(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(baseRef))
            Add(ChangedFiles.Parse((await GitAsync($"diff --name-only {baseRef}...HEAD", workspacePath, cancellationToken)).StandardOutput));

        // Uncommitted work is the common case in the developer loop, so it is always part of the
        // scope - a base ref alone would miss the edit that was just made.
        Add(ChangedFiles.ParsePorcelain((await GitAsync("status --porcelain", workspacePath, cancellationToken)).StandardOutput));
        return paths;
    }

    private Task<ProcessRunResult> GitAsync(string arguments, string workspacePath, CancellationToken cancellationToken)
        => _runner.RunAsync(new ProcessRunRequest("git", arguments, workspacePath, StepTimeoutMs), cancellationToken);

    private async Task<StepOutcome> StepAsync(string commandLine, string workingDirectory, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = CommandInvocation.ForNpmShim(commandLine);
        var result = await _runner.RunAsync(
            new ProcessRunRequest(fileName, arguments, workingDirectory, StepTimeoutMs),
            cancellationToken);

        if (result.TimedOut)
            return new StepOutcome(false, $"Timed out: {commandLine} exceeded the fast lane budget of {StepTimeoutMs / 1000}s");
        if (result.ExitCode == 0)
            return new StepOutcome(true, string.Empty);

        return new StepOutcome(false, Tail(TestOutputNormalizer.Normalize(result.StandardOutput + "\n" + result.StandardError)));
    }

    private sealed record StepOutcome(bool Passed, string Evidence);

    private static string Tail(string text)
        => text.Length <= MaxEvidenceChars ? text : "... earlier output trimmed ...\n" + text[^MaxEvidenceChars..];
}
