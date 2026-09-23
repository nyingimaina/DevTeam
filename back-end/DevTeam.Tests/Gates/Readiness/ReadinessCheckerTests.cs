using DevTeam.Broker.Gates;
using DevTeam.Broker.Gates.Readiness;

namespace DevTeam.Tests.Gates.Readiness;

public class ReadinessCheckerTests : IDisposable
{
    private const string Workspace = @"C:\work\proj";
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Passes_WhenEveryPhasePasses()
    {
        var checker = CreateChecker(_ => Ok("Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5"));

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([
                Phase("build", "The project builds", "dotnet build"),
                Phase("test", "The tests pass", "dotnet test", dependsOn: ["build"]),
            ]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal(2, report.PassedCount);
        Assert.Equal(0, report.FailedCount);
        // Metrics from the test phase are carried through for the report/charts.
        Assert.Equal(5, report.Phases.Single(p => p.Id == "test").Metrics.TestsPassed);
    }

    [Fact]
    public async Task Fails_WhenARequiredPhaseFails()
    {
        var checker = CreateChecker(request =>
            request.Arguments.Contains("dotnet test") ? Bad() : Ok());

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([
                Phase("build", "The project builds", "dotnet build"),
                Phase("test", "The tests pass", "dotnet test", dependsOn: ["build"]),
            ]),
            Workspace, ReadinessScope.Feature, "feat-001", CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(ReadinessCheckStatus.Failed, report.Phases.Single(p => p.Id == "test").Status);
    }

    [Fact]
    public async Task SkipsDependentsOfAFailedPhase_AndStillFailsOverall()
    {
        var calls = new List<string>();
        var checker = CreateChecker(request =>
        {
            calls.Add(request.Arguments);
            return request.Arguments.Contains("tsc") ? Bad() : Ok();
        });

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([
                Phase("typecheck", "No type errors", "npx tsc --noEmit"),
                Phase("build", "The website builds", "npx next build", dependsOn: ["typecheck"]),
            ]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        var build = report.Phases.Single(p => p.Id == "build");
        Assert.Equal(ReadinessCheckStatus.Skipped, build.Status);
        Assert.Contains("typecheck", build.Reason);
        Assert.False(report.Passed);
        // The dependent command never ran.
        Assert.DoesNotContain(calls, a => a.Contains("next build"));
    }

    [Fact]
    public async Task SkipsPhasesThatNeedADatabase_AndThatSkipDoesNotBlock()
    {
        var checker = CreateChecker(_ => Ok(), databaseReachable: false);

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([Phase("integration", "Works against a database", "dotnet test", skipWhen: ReadinessDefaults.SkipWhenDatabaseUnreachable)]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        Assert.Equal(ReadinessCheckStatus.Skipped, Assert.Single(report.Phases).Status);
        Assert.True(report.Passed);
    }

    [Fact]
    public async Task OptionalPhaseFailure_IsRecordedButDoesNotBlock()
    {
        var checker = CreateChecker(request => request.Arguments.Contains("lint") ? Bad() : Ok());

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([Phase("lint", "House rules", "npx next lint", required: false)]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        Assert.Equal(ReadinessCheckStatus.Failed, Assert.Single(report.Phases).Status);
        Assert.True(report.Passed);
    }

    [Fact]
    public async Task Fails_WhenCoverageIsBelowTheConfiguredFloor()
    {
        var workspace = NewWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace, "coverage"));
        File.WriteAllText(
            Path.Combine(workspace, "coverage", "coverage-summary.json"),
            """{ "total": { "lines": { "pct": 40 }, "branches": { "pct": 40 }, "functions": { "pct": 40 } } }""");
        var checker = CreateChecker(_ => Ok());

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([
                new ReadinessPhaseDefinition(
                    "tests", "The screen tests pass", "npx jest", Coverage: new ReadinessCoverageSpec("coverage/coverage-summary.json", LineMin: 80)),
            ]),
            workspace, ReadinessScope.Release, null, CancellationToken.None);

        var phase = Assert.Single(report.Phases);
        Assert.Equal(ReadinessCheckStatus.Failed, phase.Status);
        Assert.Contains("below the required 80%", phase.Reason);
        Assert.Equal(40, phase.Metrics.LineCoverage);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task RunsCommandsThroughThePlatformShell()
    {
        var calls = new List<ProcessRunRequest>();
        var checker = CreateChecker(request => { calls.Add(request); return Ok(); });

        await checker.RunPhasesAsync(
            new ReadinessProfile([Phase("test", "The tests pass", "npx jest --coverage")]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        var call = Assert.Single(calls);
        Assert.Equal(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", call.FileName);
        Assert.Contains("npx jest --coverage", call.Arguments);
    }

    [Fact]
    public async Task Fails_WhenAProcessTimesOut()
    {
        var checker = CreateChecker(_ => new ProcessRunResult(-1, "still going…", "", true, TimeSpan.FromMinutes(15)));

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([Phase("build", "The website builds", "npx next build")]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        var phase = Assert.Single(report.Phases);
        Assert.Equal(ReadinessCheckStatus.Failed, phase.Status);
        Assert.Contains("Timed out", phase.Reason);
    }

    [Fact]
    public async Task IndependentPhaseGroups_RunConcurrentlyNotSequentially()
    {
        // Two phases with no DependsOn relationship to each other (e.g. the backend and frontend
        // groups) must overlap in wall-clock time, not run back-to-back — otherwise a workspace
        // with both pays their full combined cost every time instead of the slower of the two.
        var entered = 0;
        var bothEntered = new TaskCompletionSource();

        async Task<ProcessRunResult> Handler(ProcessRunRequest request)
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return Ok();
        }

        var checker = new ReadinessChecker(new AsyncFakeProcessRunner(Handler), new FakeReadinessEnvironment(true));

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([
                Phase("backend-build", "The backend builds", "dotnet build"),
                Phase("frontend-typecheck", "No type errors", "npx tsc --noEmit"),
            ]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal(2, entered);
    }

    [Fact]
    public async Task DependentPhaseGroups_StillRunInDependencyOrder_EvenAlongsideAnIndependentGroup()
    {
        // A dependency chain (typecheck -> build) must still run strictly in order — parallelism
        // is only across INDEPENDENT groups, never within one.
        var calls = new List<string>();
        var checker = CreateChecker(request =>
        {
            lock (calls) calls.Add(request.Arguments);
            return request.Arguments.Contains("tsc") ? Bad() : Ok();
        });

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([
                Phase("backend-build", "The backend builds", "dotnet build"),
                Phase("typecheck", "No type errors", "npx tsc --noEmit"),
                Phase("build", "The website builds", "npx next build", dependsOn: ["typecheck"]),
            ]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        var build = report.Phases.Single(p => p.Id == "build");
        Assert.Equal(ReadinessCheckStatus.Skipped, build.Status);
        Assert.Contains("typecheck", build.Reason);
        Assert.DoesNotContain(calls, a => a.Contains("next build"));
    }

    [Fact]
    public async Task ReportPreservesTheProfilesDeclaredPhaseOrder_RegardlessOfWhichGroupFinishesFirst()
    {
        var report = await CreateChecker(_ => Ok()).RunPhasesAsync(
            new ReadinessProfile([
                Phase("backend-build", "...", "dotnet build"),
                Phase("backend-unit", "...", "dotnet test", dependsOn: ["backend-build"]),
                Phase("frontend-typecheck", "...", "npx tsc --noEmit"),
            ]),
            Workspace, ReadinessScope.Release, null, CancellationToken.None);

        Assert.Equal(["backend-build", "backend-unit", "frontend-typecheck"], report.Phases.Select(p => p.Id));
    }

    [Fact]
    public async Task RunsRealCommandsThroughThePlatformShell()
    {
        // The real SystemProcessRunner + shell wrapping (no fake): proves a readiness command
        // actually executes. A harmless echo keeps it fast and portable.
        var checker = new ReadinessChecker(new SystemProcessRunner(), new FakeReadinessEnvironment(true));

        var report = await checker.RunPhasesAsync(
            new ReadinessProfile([new ReadinessPhaseDefinition("echo", "Echo", "echo readiness-ok")]),
            Path.GetTempPath(), ReadinessScope.Release, null, CancellationToken.None);

        var phase = Assert.Single(report.Phases);
        Assert.True(report.Passed, phase.Reason);
        Assert.Contains("readiness-ok", phase.RawOutput);
    }

    private static ReadinessPhaseDefinition Phase(
        string id, string title, string command, IReadOnlyList<string>? dependsOn = null,
        bool required = true, string? skipWhen = null)
        => new(id, title, command, DependsOn: dependsOn, Required: required, SkipWhen: skipWhen);

    private static ProcessRunResult Ok(string output = "") => new(0, output, "", false, TimeSpan.FromMilliseconds(5));

    private static ProcessRunResult Bad(int exitCode = 1, string output = "") => new(exitCode, output, "", false, TimeSpan.FromMilliseconds(5));

    private static ReadinessChecker CreateChecker(Func<ProcessRunRequest, ProcessRunResult> handler, bool databaseReachable = true)
        => new(new FakeProcessRunner(handler), new FakeReadinessEnvironment(databaseReachable));

    private string NewWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "devteam-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class FakeReadinessEnvironment(bool databaseReachable) : IReadinessEnvironment
    {
        public bool IsDatabaseReachable() => databaseReachable;
    }

    // FakeProcessRunner (elsewhere in this test project) wraps its handler in Task.FromResult, so
    // it never genuinely suspends — every call finishes synchronously before the next one starts,
    // which can't prove real concurrency. This one awaits its handler for real, the way the
    // production SystemProcessRunner awaiting actual process I/O does.
    private sealed class AsyncFakeProcessRunner(Func<ProcessRunRequest, Task<ProcessRunResult>> handler) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
            => handler(request);
    }
}
