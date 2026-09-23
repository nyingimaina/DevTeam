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
}
