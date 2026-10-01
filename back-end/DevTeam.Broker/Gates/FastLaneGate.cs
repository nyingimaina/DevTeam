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
public sealed class FastLaneGate : IGate, IReusableGate
{
    private const int StepTimeoutMs = 180_000;
    private const int MaxEvidenceChars = 4000;

    private readonly IProcessRunner _runner;
    private readonly GitChangeSet _changes;

    public FastLaneGate(IProcessRunner runner)
    {
        _runner = runner;
        _changes = new GitChangeSet(runner);
    }

    public string Name => BuiltinRegistry.FastLane;

    /// <summary>
    /// Keyed on the content of every changed file, because that set is exactly what this gate reads
    /// (the C# incremental build, the type check and the scoped tests all key off it). Edit one of
    /// those files and the key changes, so the next check re-runs; leave the tree alone and the
    /// recorded verdict stands.
    /// </summary>
    public async Task<string?> ReuseKeyAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var workspace = request.WorkspacePath;
        var baseRef = GateInputs.GetOptional(request.Inputs, "baseRef");
        var changed = await _changes.ChangedPathsAsync(workspace, baseRef, cancellationToken);
        return GateCache.ForChangedFiles(workspace, changed, baseRef, Name);
    }

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var workspace = request.WorkspacePath;
        var baseRef = GateInputs.GetOptional(request.Inputs, "baseRef");
        var changed = await _changes.ChangedPathsAsync(workspace, baseRef, cancellationToken);

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
