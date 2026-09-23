using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class SliceManifestTests
{
    [Fact]
    public void EffectiveCodePaths_NoFreeFormPathsSet_FallsBackToBackAndFront()
    {
        // Backward compatibility: a manifest scaffolded before free-form paths existed (or one
        // that never needed anything beyond the classic split) still resolves the same two
        // paths it always did.
        var manifest = new SliceManifest("feat-001", "Feature", "back-end/**/Features/<F>", "front-end/app/<F>", [], "dotnet test");

        Assert.Equal(["back-end/**/Features/<F>", "front-end/app/<F>"], manifest.EffectiveCodePaths);
    }

    [Fact]
    public void EffectiveCodePaths_BackOrFrontEmpty_OmitsTheEmptyOne()
    {
        var manifest = new SliceManifest("feat-001", "Feature", "back-end/src/Features/<F>", "", [], "dotnet test");

        Assert.Equal(["back-end/src/Features/<F>"], manifest.EffectiveCodePaths);
    }

    [Fact]
    public void FreeFormCodePaths_TakesPrecedenceOverBackAndFront()
    {
        // A feature whose app doesn't split into backend/frontend at all (e.g. a single WPF
        // project) declares its own arbitrary list of code paths instead.
        var manifest = new SliceManifest(
            "feat-001", "Feature", ["src/Features/<F>", "src/Features/<F>.Tests"], [], "dotnet test");

        Assert.Equal(["src/Features/<F>", "src/Features/<F>.Tests"], manifest.EffectiveCodePaths);
    }

    [Fact]
    public void FreeFormConstructor_AlsoPopulatesLegacyBackAndFrontForDisplayAndDbColumns()
    {
        var manifest = new SliceManifest(
            "feat-001", "Feature", ["src/Features/<F>", "src/Features/<F>.Tests"], [], "dotnet test");

        Assert.Equal("src/Features/<F>", manifest.CodePathBack);
        Assert.Equal("src/Features/<F>.Tests", manifest.CodePathFront);
    }

    [Fact]
    public void RoundTripsThroughYaml_PreservesFreeFormCodePaths()
    {
        var dir = Directory.CreateTempSubdirectory("slice-manifest-roundtrip-");
        try
        {
            var path = Path.Combine(dir.FullName, "manifest.yaml");
            var manifest = new SliceManifest(
                "feat-001", "Feature", ["src/Features/<F>", "src/Features/<F>.Tests"], [], "dotnet test");
            SliceManifestIO.Write(path, manifest);

            var reread = SliceManifestIO.TryRead(path);

            Assert.NotNull(reread);
            Assert.Equal(["src/Features/<F>", "src/Features/<F>.Tests"], reread!.EffectiveCodePaths);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }
}
