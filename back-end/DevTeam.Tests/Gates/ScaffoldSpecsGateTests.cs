using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;
using DevTeam.Tests.Context;

namespace DevTeam.Tests.Gates;

public class ScaffoldSpecsGateTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private GateRequest Request(string featureKey = "feat-001") => new(BuiltinRegistry.ScaffoldSpecs, _workspace.Path, featureKey);

    [Fact]
    public async Task NoExistingManifest_ScaffoldsAndPasses()
    {
        var gate = new ScaffoldSpecsGate();

        var result = await gate.RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.True(File.Exists(ArtifactPaths.ManifestPath(_workspace.Path, "feat-001")));
    }

    [Fact]
    public async Task ManifestFromADifferentFeature_Fails()
    {
        // A stray/contaminated manifest under this feature's own path but for a different
        // feature key is exactly the "something's wrong" case this gate exists to catch.
        var manifestPath = ArtifactPaths.ManifestPath(_workspace.Path, "feat-001");
        SliceManifestIO.Write(manifestPath, new SliceManifest(
            "some-other-feature", "some-other-feature", "back-end/**/Features/<F>", "front-end/app/<F>", [], "dotnet test"));

        var gate = new ScaffoldSpecsGate();
        var result = await gate.RunAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task RetryOfTheSameFeature_ManifestAlreadyMatches_PassesWithoutRewriting()
    {
        // RetryStageAsync opens a fresh business-analyst attempt for a feature that already
        // scaffolded successfully on an earlier attempt — its manifest is valid and matches this
        // same feature key. That must not be treated as "refuse to overwrite an existing
        // scaffold" (a previous, real bug: retrying a stuck business-analyst stage always failed
        // its own entry gate with "Refusing to overwrite existing scaffold").
        var manifestPath = ArtifactPaths.ManifestPath(_workspace.Path, "feat-001");
        var original = new SliceManifest(
            "feat-001", "feat-001", "back-end/**/Features/<F>", "front-end/app/<F>", [], "dotnet test DevTeam.slnx");
        SliceManifestIO.Write(manifestPath, original);
        var writtenAt = File.GetLastWriteTimeUtc(manifestPath);

        var gate = new ScaffoldSpecsGate();
        var result = await gate.RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(manifestPath));
    }

    [Fact]
    public async Task CodePathsInput_ScaffoldsAFreeFormManifest()
    {
        // An app that doesn't split into backend/frontend (a single WPF/console/library project)
        // declares its own arbitrary code path list instead of the classic two-slot split.
        var inputs = new Dictionary<string, string> { ["codePaths"] = "src/Features/<F>;src/Features/<F>.Tests" };
        var gate = new ScaffoldSpecsGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ScaffoldSpecs, _workspace.Path, "feat-001", "business-analyst", inputs),
            CancellationToken.None);

        Assert.True(result.Passed);
        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(_workspace.Path, "feat-001"));
        Assert.NotNull(manifest);
        Assert.Equal(["src/Features/<F>", "src/Features/<F>.Tests"], manifest!.EffectiveCodePaths);
    }

    [Fact]
    public async Task NoCodePathsInput_FallsBackToClassicBackAndFrontDefaults()
    {
        var gate = new ScaffoldSpecsGate();

        var result = await gate.RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(_workspace.Path, "feat-001"));
        Assert.NotNull(manifest);
        Assert.Equal(["back-end/**/Features/<F>", "front-end/app/<F>"], manifest!.EffectiveCodePaths);
    }

    [Fact]
    public async Task PluralCorePathsInput_SetsTheManifestsFreeFormCorePaths()
    {
        // A single-project app's shared core doesn't split into "back" and "front" either — the
        // free-form branch should read a plural corePaths input, not always corePathBack/Front.
        var inputs = new Dictionary<string, string>
        {
            ["codePaths"] = "src/Features/<F>",
            ["corePaths"] = "src/Core;src/Core.Tests",
        };
        var gate = new ScaffoldSpecsGate();

        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ScaffoldSpecs, _workspace.Path, "feat-001", "business-analyst", inputs),
            CancellationToken.None);

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(_workspace.Path, "feat-001"));
        Assert.NotNull(manifest);
        Assert.Equal(["src/Core", "src/Core.Tests"], manifest!.EffectiveCorePaths);
    }

    [Fact]
    public async Task FreeFormBranch_NoPluralCorePathsInput_FallsBackToCorePathBackAndFrontDefaults()
    {
        var inputs = new Dictionary<string, string> { ["codePaths"] = "src/Features/<F>" };
        var gate = new ScaffoldSpecsGate();

        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ScaffoldSpecs, _workspace.Path, "feat-001", "business-analyst", inputs),
            CancellationToken.None);

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(_workspace.Path, "feat-001"));
        Assert.NotNull(manifest);
        Assert.Equal([CorePaths.DefaultBack, CorePaths.DefaultFront], manifest!.EffectiveCorePaths);
    }

    [Fact]
    public async Task ProjectTypeGiven_WritesTheWorkspaceProfileOnce()
    {
        var inputs = new Dictionary<string, string>
        {
            ["projectType"] = "wpf-desktop",
            ["codePaths"] = "src/Features/<F>",
            ["buildCommand"] = "dotnet build Calculator.slnx",
            ["testCommand"] = "dotnet test Calculator.slnx",
        };
        var gate = new ScaffoldSpecsGate();

        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ScaffoldSpecs, _workspace.Path, "feat-001", "business-analyst", inputs),
            CancellationToken.None);

        var profile = ProjectProfileIO.TryRead(_workspace.Path);
        Assert.NotNull(profile);
        Assert.Equal("wpf-desktop", profile!.ProjectType);
        Assert.Equal("dotnet build Calculator.slnx", profile.BuildCommand);
        Assert.Equal("dotnet test Calculator.slnx", profile.TestCommand);
    }

    [Fact]
    public async Task NoProjectTypeAndNoCodePaths_DoesNotWriteAProfile()
    {
        // The classic backend/frontend split has nothing non-default to declare — leaving the
        // profile absent means readiness detection keeps convention-sniffing unchanged.
        var gate = new ScaffoldSpecsGate();

        await gate.RunAsync(Request(), CancellationToken.None);

        Assert.False(ProjectProfileIO.Exists(_workspace.Path));
    }

    [Fact]
    public async Task ProfileAlreadyExists_SecondFeatureNeverOverwritesIt()
    {
        ProjectProfileIO.Write(_workspace.Path, new ProjectProfile { ProjectType = "original-type" });
        var writtenAt = File.GetLastWriteTimeUtc(ProjectProfileIO.ProfilePath(_workspace.Path));

        var inputs = new Dictionary<string, string> { ["projectType"] = "a-different-type" };
        var gate = new ScaffoldSpecsGate();
        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.ScaffoldSpecs, _workspace.Path, "feat-002", "business-analyst", inputs),
            CancellationToken.None);

        var profile = ProjectProfileIO.TryRead(_workspace.Path);
        Assert.Equal("original-type", profile!.ProjectType);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(ProjectProfileIO.ProfilePath(_workspace.Path)));
    }
}
