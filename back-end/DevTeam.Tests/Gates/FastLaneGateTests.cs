using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class ChangedFilesTests
{
    [Fact]
    public void Parse_TrimsLinesAndDropsBlanks()
    {
        var parsed = ChangedFiles.Parse("front-end/app/a.tsx\r\n  back-end/A.cs  \r\n\r\nfront-end/app/b.ts\r\n");

        Assert.Equal(["front-end/app/a.tsx", "back-end/A.cs", "front-end/app/b.ts"], parsed);
    }

    [Fact]
    public void ParsePorcelain_StripsStatusColumnsAndKeepsTheNewPathOfARename()
    {
        var parsed = ChangedFiles.ParsePorcelain(
            " M front-end/app/a.tsx\n?? front-end/app/b.ts\nR  old/Widget.tsx -> front-end/app/Widget.tsx\n");

        Assert.Equal(["front-end/app/a.tsx", "front-end/app/b.ts", "front-end/app/Widget.tsx"], parsed);
    }

    [Theory]
    [InlineData("front-end/app/a.tsx", true)]
    [InlineData("front-end/app/a.ts", true)]
    [InlineData("front-end/app/a.jsx", true)]
    [InlineData("front-end/jest.config.js", true)]
    [InlineData("front-end/app/a.mjs", true)]
    [InlineData("back-end/A.cs", false)]
    [InlineData("README.md", false)]
    public void IsTypeScript_RecognizesJsFamily(string path, bool expected)
        => Assert.Equal(expected, ChangedFiles.IsTypeScript(path));

    [Theory]
    [InlineData("back-end/A.cs", true)]
    [InlineData("back-end/DevTeam.Broker/DevTeam.Broker.csproj", true)]
    [InlineData("back-end/DevTeam.slnx", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("front-end/app/a.tsx", false)]
    public void IsDotNet_RecognizesCSharpFamily(string path, bool expected)
        => Assert.Equal(expected, ChangedFiles.IsDotNet(path));

    [Fact]
    public void FrontendRoot_WalksUpToTheNearestPackageJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "devteam-fr-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "app", "Project", "UI");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, "package.json"), "{}");

        var resolved = ChangedFiles.FrontendRoot(root, ["app/Project/UI/Wizard.tsx"]);

        Assert.Equal(root, resolved);
    }

    [Fact]
    public void FrontendRoot_IsNull_WhenNoPackageJsonAnywhereUpTheTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "devteam-fr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app"));

        var resolved = ChangedFiles.FrontendRoot(root, ["app/Orphan.tsx"]);

        Assert.Null(resolved);
    }
}

public class CommandInvocationTests
{
    [Fact]
    public void NpmShim_OnWindows_GoesThroughCmd()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (fileName, arguments) = CommandInvocation.ForNpmShim("npx jest --changedSince=HEAD");

        Assert.Equal("cmd.exe", fileName);
        Assert.Equal("/c npx jest --changedSince=HEAD", arguments);
    }
}

public class FastLaneGateTests
{
    private static string CreateWorkspace(bool withFrontend = true, bool withSolution = false)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-fastlane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspace, "feat-001"));
        if (withFrontend)
        {
            Directory.CreateDirectory(Path.Combine(workspace, "front-end", "app"));
            File.WriteAllText(Path.Combine(workspace, "front-end", "package.json"), "{}");
            File.WriteAllText(Path.Combine(workspace, "front-end", "tsconfig.json"), "{}");
        }

        if (withSolution)
            File.WriteAllText(Path.Combine(workspace, "DevTeam.slnx"), "<solution />");
        return workspace;
    }

    private static FakeProcessRunner Scripted(Dictionary<string, ProcessRunResult> byNeedle, ProcessRunResult? fallback = null)
        => new(request =>
        {
            var line = request.Arguments;
            foreach (var (needle, result) in byNeedle)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return result;
            }

            return fallback ?? new ProcessRunResult(0, string.Empty, string.Empty, false, TimeSpan.Zero);
        });

    [Fact]
    public async Task Passes_WithoutRunningAnything_WhenNothingChanged()
    {
        var workspace = CreateWorkspace();
        var runner = FakeProcessRunner.Git(string.Empty);
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("tsc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TypeScriptOnlyChange_RunsTypeCheckThenScopedJest()
    {
        var workspace = CreateWorkspace();
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["diff --name-only"] = Ok("front-end/app/Project/UI/Wizard.tsx\n"),
            ["tsc"] = Ok(string.Empty),
            ["jest"] = Ok("Tests: 0 failed, 4 passed, 4 total\n"),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
        var tsc = Assert.Single(runner.Calls, c => c.Arguments.Contains("tsc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("front-end", tsc.WorkingDirectory);
        var jest = Assert.Single(runner.Calls, c => c.Arguments.Contains("jest", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--changedSince=release/feat-001", jest.Arguments);
    }

    [Fact]
    public async Task TypeCheckFailure_StopsBeforeScopedTests()
    {
        var workspace = CreateWorkspace();
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["diff --name-only"] = Ok("front-end/app/Project/UI/Wizard.tsx\n"),
            ["tsc"] = new ProcessRunResult(2, string.Empty, "Wizard.tsx(41,18): error TS2322: Type 'string' is not assignable", false, TimeSpan.Zero),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("TS2322", result.EvidenceText);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("jest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScopedTestFailure_ReportsTheFailedTestName()
    {
        var workspace = CreateWorkspace();
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["diff --name-only"] = Ok("front-end/app/Project/UI/Wizard.tsx\n"),
            ["tsc"] = Ok(string.Empty),
            ["jest"] = new ProcessRunResult(1, "  \u25CF Wizard \u203A saves\nTests: 1 failed, 3 passed, 4 total\n", string.Empty, false, TimeSpan.Zero),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("fail: Wizard \u203A saves", result.EvidenceText);
    }

    [Fact]
    public async Task ScopedTests_MatchNothingChanged_StillPasses()
    {
        var workspace = CreateWorkspace();
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["diff --name-only"] = Ok("front-end/app/Project/UI/Wizard.tsx\n"),
            ["tsc"] = Ok(string.Empty),
            ["jest"] = new ProcessRunResult(0, "No tests found related to files changed since HEAD.\n", string.Empty, false, TimeSpan.Zero),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
    }

    [Fact]
    public async Task CSharpOnlyChange_DelegatesToTheIncrementalBuild()
    {
        var workspace = CreateWorkspace(withFrontend: false, withSolution: true);
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["diff --name-only"] = Ok("back-end/A.cs\n"),
            ["build"] = Ok("Build succeeded.\n"),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
        var build = Assert.Single(runner.Calls, c => c.Arguments.Contains("build", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("dotnet", build.FileName);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("jest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TypeScriptChange_WithNoFrontendProject_SkipsTheTypeChecks()
    {
        var workspace = CreateWorkspace(withFrontend: false);
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["diff --name-only"] = Ok("docs/notes.ts\n"),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("tsc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TimedOutStep_Fails()
    {
        var workspace = CreateWorkspace();
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["diff --name-only"] = Ok("front-end/app/Project/UI/Wizard.tsx\n"),
            ["tsc"] = new ProcessRunResult(-1, string.Empty, string.Empty, true, TimeSpan.FromMinutes(3)),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["baseRef"] = "release/feat-001" }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Timed out", result.Reason + result.EvidenceText);
    }

    [Fact]
    public async Task UncommittedChanges_AreIncluded_EvenWithoutABaseRef()
    {
        var workspace = CreateWorkspace();
        var runner = Scripted(new Dictionary<string, ProcessRunResult>
        {
            ["status --porcelain"] = Ok(" M front-end/app/Project/UI/Wizard.tsx\n"),
            ["tsc"] = Ok(string.Empty),
            ["jest"] = Ok("Tests: 0 failed, 1 passed, 1 total\n"),
        });
        var gate = new FastLaneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.FastLane, workspace, "feat-001", "developer"),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
        var jest = Assert.Single(runner.Calls, c => c.Arguments.Contains("jest", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--passWithNoTests", jest.Arguments);
    }

    private static ProcessRunResult Ok(string output) => new(0, output, string.Empty, false, TimeSpan.Zero);
}
