using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class ScaffoldAndContextGateTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-gates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GateRequest Request(string workspace, string featureKey = "feat-001", IReadOnlyDictionary<string, string>? inputs = null)
        => new(BuiltinRegistry.ScaffoldSpecs, workspace, featureKey, "business-analyst", inputs);

    [Fact]
    public async Task Scaffold_WritesManifestAndRequirements()
    {
        var gate = new ScaffoldSpecsGate();
        var inputs = new Dictionary<string, string>
        {
            ["title"] = "Login",
            ["requirementsJson"] =
                """[{"id":"REQ-001","title":"User can log in","acceptanceCriteria":"Given a registered user, When they enter valid credentials, Then they are signed in"}]""",
            ["testCommand"] = "dotnet test DevTeam.slnx",
        };

        var result = await gate.RunAsync(Request(_workspace, inputs: inputs), CancellationToken.None);

        Assert.True(result.Passed);
        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(_workspace, "feat-001"));
        Assert.NotNull(manifest);
        Assert.Equal("Login", manifest!.Title);
        var requirements = System.IO.File.ReadAllText(ArtifactPaths.BrsPath(_workspace, "feat-001"));
        Assert.Contains("REQ-001: User can log in", requirements);
        Assert.Contains("Given a registered user", requirements);
    }

    [Fact]
    public async Task Scaffold_RefusesToOverwriteAManifestForADifferentFeature()
    {
        // A manifest sitting at this feature's path but stamped for another feature key is a
        // real contamination case — refuse it. (A manifest that matches THIS feature's own key
        // is a legitimate retry of an already-scaffolded feature and must pass instead — see
        // ScaffoldSpecsGateTests.RetryOfTheSameFeature_ManifestAlreadyMatches_PassesWithoutRewriting.)
        var gate = new ScaffoldSpecsGate();
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(_workspace, "feat-001"));
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(_workspace, "feat-001"),
            new SliceManifest("some-other-feature", "Login", "back", "front", [], "test"));

        var result = await gate.RunAsync(Request(_workspace), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("already exists", result.Reason);
    }

    [Fact]
    public async Task Scaffold_DoesNotOverwriteExistingRequirementsFile()
    {
        var gate = new ScaffoldSpecsGate();
        var brsPath = ArtifactPaths.BrsPath(_workspace, "feat-001");
        Directory.CreateDirectory(Path.GetDirectoryName(brsPath)!);
        await File.WriteAllTextAsync(brsPath,
            "## REQ-9: Authored by the analyst" + Environment.NewLine +
            "Given a registered user" + Environment.NewLine +
            "Then they are signed in");

        var inputs = new Dictionary<string, string>
        {
            ["requirementsJson"] =
                """[{"id":"REQ-1","title":"Scaffold default","acceptanceCriteria":"Given x, When y, Then z"}]""",
        };
        var result = await gate.RunAsync(Request(_workspace, inputs: inputs), CancellationToken.None);

        Assert.True(result.Passed);
        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(_workspace, "feat-001"));
        Assert.NotNull(manifest);
        var requirements = File.ReadAllText(brsPath);
        Assert.Contains("REQ-9: Authored by the analyst", requirements);
        Assert.DoesNotContain("REQ-1", requirements);
    }

    [Fact]
    public async Task Context_RequiresScaffoldFirst()
    {
        var gate = new ContextBundleGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ContextBundle, _workspace, "feat-001"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("no slice manifest", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Context_WritesBundleWithRules()
    {
        var scaffold = new ScaffoldSpecsGate();
        await scaffold.RunAsync(Request(_workspace), CancellationToken.None);

        var gate = new ContextBundleGate();
        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ContextBundle, _workspace, "feat-001", "business-analyst"),
            CancellationToken.None);

        Assert.True(result.Passed);
        var context = System.IO.File.ReadAllText(ArtifactPaths.ContextPath(_workspace, "feat-001"));
        Assert.Contains("Approved requirements", context);
        Assert.Contains("Allowed working areas", context);
        Assert.Contains("Add tests first", context);
        Assert.Equal(result.ArtifactPath, Path.GetRelativePath(_workspace, ArtifactPaths.ContextPath(_workspace, "feat-001")));
    }

    [Fact]
    public async Task Context_IncludesTheSharedCoreMap()
    {
        var scaffold = new ScaffoldSpecsGate();
        await scaffold.RunAsync(Request(_workspace), CancellationToken.None);
        var coreDir = Path.Combine(_workspace, "back-end", "src", "Core");
        Directory.CreateDirectory(coreDir);

        var gate = new ContextBundleGate();
        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ContextBundle, _workspace, "feat-001", "developer"),
            CancellationToken.None);

        var context = System.IO.File.ReadAllText(ArtifactPaths.ContextPath(_workspace, "feat-001"));
        Assert.Contains("Shared core (REUSE BEFORE WRITING)", context);
        Assert.Contains(coreDir.Replace('\\', '/'), context);
    }

    [Fact]
    public async Task Context_FreeFormManifestWithMoreThanTwoCodePaths_ListsAllOfThemNotJustTheFirstTwo()
    {
        // A single-project app (e.g. WPF) can declare any number of code paths — the context
        // bundle must not silently drop everything past the first two under a hardcoded
        // "Backend:"/"Frontend:" pair (a previous, real bug: downstream stages only ever saw
        // two of N declared paths).
        var manifestPath = ArtifactPaths.ManifestPath(_workspace, "feat-001");
        SliceManifestIO.Write(manifestPath, new SliceManifest(
            "feat-001", "Calculator",
            ["src/Features/calc", "src/Features/calc.Tests", "src/Features/calc.Resources"],
            [], "dotnet test"));

        var gate = new ContextBundleGate();
        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ContextBundle, _workspace, "feat-001", "developer"),
            CancellationToken.None);

        var context = System.IO.File.ReadAllText(ArtifactPaths.ContextPath(_workspace, "feat-001"));
        Assert.Contains("src/Features/calc`", context);
        Assert.Contains("src/Features/calc.Tests`", context);
        Assert.Contains("src/Features/calc.Resources`", context);
    }

    [Fact]
    public async Task Context_FreeFormCorePathsWithNoDirsOnDiskYet_ListsAllOfThemNotJustBackAndFront()
    {
        // Same gap on the shared-core side: before any core directory has been created on disk
        // (CoreSourcePaths finds nothing yet), the fallback line must list every declared core
        // path, not just a hardcoded back/front pair.
        var manifestPath = ArtifactPaths.ManifestPath(_workspace, "feat-001");
        var manifest = new SliceManifest("feat-001", "Calculator", "src/Features/calc", "", [], "dotnet test")
        {
            CorePaths = ["src/Core", "src/Core.Tests", "src/Core.Resources"],
        };
        SliceManifestIO.Write(manifestPath, manifest);

        var gate = new ContextBundleGate();
        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ContextBundle, _workspace, "feat-001", "developer"),
            CancellationToken.None);

        var context = System.IO.File.ReadAllText(ArtifactPaths.ContextPath(_workspace, "feat-001"));
        Assert.Contains("src/Core`", context);
        Assert.Contains("src/Core.Tests`", context);
        Assert.Contains("src/Core.Resources`", context);
    }

    [Fact]
    public void TryRead_MalformedYaml_ReturnsNullInsteadOfThrowing()
    {
        // manifest.yaml lives under the feature's own artifacts directory, so any role's agent
        // can hand-edit it — an unquoted colon in a value (e.g. a URI in testCommand) is enough
        // to break the parser. That must degrade to "no manifest" rather than crash the caller.
        var manifestPath = ArtifactPaths.ManifestPath(_workspace, "feat-001");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        System.IO.File.WriteAllText(manifestPath,
            "feature: feat-001\ntestCommand: \"C:/tools/run.exe\" --arg res://tests/run_tests.gd\n");

        var manifest = SliceManifestIO.TryRead(manifestPath);

        Assert.Null(manifest);
    }
}