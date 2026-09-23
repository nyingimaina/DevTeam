using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class DeveloperScopeSnapshotTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-scope-snapshot-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Path_IsOutsideTheFeatureDirectory()
    {
        // The whole point: devteam/features/<F> is inside the developer's own write scope, so a
        // snapshot stored there could be self-edited exactly like manifest.yaml is today.
        var featureDir = ArtifactPaths.FeatureDirRelative("feat-001").Replace('/', Path.DirectorySeparatorChar);
        var snapshotPath = DeveloperScopeSnapshotIO.Path(_workspace, "feat-001");

        Assert.DoesNotContain(featureDir, snapshotPath);
    }

    [Fact]
    public void TryRead_NoFile_ReturnsNull()
    {
        Assert.Null(DeveloperScopeSnapshotIO.TryRead(_workspace, "feat-001"));
    }

    [Fact]
    public void WriteIfAbsent_ThenTryRead_RoundTrips()
    {
        var snapshot = new DeveloperScopeSnapshot(["src/Features/calc"], "src/Core", "", ["Calculator.csproj"]);

        DeveloperScopeSnapshotIO.WriteIfAbsent(_workspace, "feat-001", snapshot);
        var read = DeveloperScopeSnapshotIO.TryRead(_workspace, "feat-001");

        Assert.NotNull(read);
        Assert.Equal(["src/Features/calc"], read!.CodePaths);
        Assert.Equal("src/Core", read.CoreBack);
        Assert.Equal(["Calculator.csproj"], read.Shared);
    }

    [Fact]
    public void WriteIfAbsent_SecondCallNeverOverwritesTheFirst()
    {
        DeveloperScopeSnapshotIO.WriteIfAbsent(_workspace, "feat-001", new DeveloperScopeSnapshot(["original"], "core-back", "core-front", []));
        DeveloperScopeSnapshotIO.WriteIfAbsent(_workspace, "feat-001", new DeveloperScopeSnapshot(["tampered"], "core-back", "core-front", []));

        var read = DeveloperScopeSnapshotIO.TryRead(_workspace, "feat-001");

        Assert.Equal(["original"], read!.CodePaths);
    }

    [Fact]
    public void TryRead_MalformedJson_ReturnsNullInsteadOfThrowing()
    {
        var path = DeveloperScopeSnapshotIO.Path(_workspace, "feat-001");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json");

        Assert.Null(DeveloperScopeSnapshotIO.TryRead(_workspace, "feat-001"));
    }
}
