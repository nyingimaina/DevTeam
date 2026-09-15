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
    public async Task Scaffold_RefusesToOverwriteExistingManifest()
    {
        var gate = new ScaffoldSpecsGate();
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(_workspace, "feat-001"));
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(_workspace, "feat-001"),
            new SliceManifest("feat-001", "Login", "back", "front", [], "test"));

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