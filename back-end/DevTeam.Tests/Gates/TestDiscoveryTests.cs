using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class TestDiscoveryTests
{
    [Fact]
    public void Discover_FindsFilesUnderTestsProjectFolder()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "CalculatorLib.Tests"));
        File.WriteAllText(Path.Combine(dir, "CalculatorLib.Tests", "CalculatorTests.cs"), "REQ-001");

        var files = TestDiscovery.Discover(dir);

        var file = Assert.Single(files);
        Assert.EndsWith("CalculatorTests.cs", file.Path);
        Assert.Equal("REQ-001", file.Content);
    }

    [Fact]
    public void Discover_ExcludesBuildAndSourceControlFolders()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "Calc.Tests", "obj"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "Calc.Tests", "bin"));
        File.WriteAllText(Path.Combine(dir, "Calc.Tests", "CalcTests.cs"), "REQ-001");
        File.WriteAllText(Path.Combine(dir, "Calc.Tests", "obj", "build.cs"), "REQ-001");
        File.WriteAllText(Path.Combine(dir, ".git", "config"), "REQ-001");

        var files = TestDiscovery.Discover(dir);

        Assert.DoesNotContain(files, f => f.Path.Contains("obj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Path.Contains("bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Path.Contains(".git", StringComparison.OrdinalIgnoreCase));
        Assert.Single(files);
    }

    [Fact]
    public void Discover_CapsFileCountAndContentLength()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "T.Tests"));
        for (var i = 0; i < 300; i++)
            File.WriteAllText(Path.Combine(dir, "T.Tests", $"T{i}.cs"), new string('x', 50_000));

        var files = TestDiscovery.Discover(dir);

        Assert.Equal(200, files.Count);
        Assert.All(files, f => Assert.True(f.Content.Length <= 16_384));
    }

    [Fact]
    public void Discover_IgnoresMissingWorkspace()
    {
        Assert.Empty(TestDiscovery.Discover(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void FilesText_ConcatenatesPathsAndContents()
    {
        var text = TestFiles.Text("""[{"path":"A.Tests/A.cs","content":"body REQ-001"}]""");

        Assert.Contains("A.Tests/A.cs", text);
        Assert.Contains("REQ-001", text);
    }

    [Fact]
    public void FilesText_ReturnsEmptyForInvalidJson()
    {
        Assert.Empty(TestFiles.Text("not json"));
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "devteam-testdiscovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}