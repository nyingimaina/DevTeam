using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class ProjectProfileTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-project-profile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void TryRead_NoFileOnDisk_ReturnsNull()
    {
        Assert.Null(ProjectProfileIO.TryRead(_workspace));
    }

    [Fact]
    public void WriteThenTryRead_RoundTripsAllFields()
    {
        var profile = new ProjectProfile
        {
            ProjectType = "wpf-desktop",
            CorePaths = ["src/Core", "src/Core.Tests"],
            BuildCommand = "dotnet build Calculator.slnx",
            TestCommand = "dotnet test Calculator.slnx",
        };

        ProjectProfileIO.Write(_workspace, profile);
        var read = ProjectProfileIO.TryRead(_workspace);

        Assert.NotNull(read);
        Assert.Equal("wpf-desktop", read!.ProjectType);
        Assert.Equal(["src/Core", "src/Core.Tests"], read.CorePaths);
        Assert.Equal("dotnet build Calculator.slnx", read.BuildCommand);
        Assert.Equal("dotnet test Calculator.slnx", read.TestCommand);
    }

    [Fact]
    public void Write_CreatesFileAtDevteamProjectProfileYaml()
    {
        ProjectProfileIO.Write(_workspace, new ProjectProfile { ProjectType = "console" });

        Assert.True(File.Exists(Path.Combine(_workspace, "devteam", "project-profile.yaml")));
    }

    [Fact]
    public void TryRead_MalformedYaml_ReturnsNullInsteadOfThrowing()
    {
        // Soft-degrade, same treatment SliceManifestIO gives a hand-edited/corrupted manifest —
        // callers fall back to convention-sniffing rather than crashing on a broken profile.
        var path = Path.Combine(_workspace, "devteam", "project-profile.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not: [valid: yaml: at all");

        Assert.Null(ProjectProfileIO.TryRead(_workspace));
    }

    [Fact]
    public void Exists_ReflectsWhetherTheFileIsOnDisk()
    {
        Assert.False(ProjectProfileIO.Exists(_workspace));

        ProjectProfileIO.Write(_workspace, new ProjectProfile { ProjectType = "console" });

        Assert.True(ProjectProfileIO.Exists(_workspace));
    }
}
