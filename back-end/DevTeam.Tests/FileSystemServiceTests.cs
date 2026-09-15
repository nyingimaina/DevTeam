using DevTeam.Broker.Server;

namespace DevTeam.Tests;

public class FileSystemServiceTests : IDisposable
{
    private readonly string _root;

    public FileSystemServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "devteam-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IFileSystemService CreateService() => new FileSystemService();

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public int CallCount { get; private set; }
        public string? LastFileName { get; private set; }
        public string? LastArgument { get; private set; }

        public void Launch(string fileName, string argument)
        {
            CallCount++;
            LastFileName = fileName;
            LastArgument = argument;
        }
    }

    [Fact]
    public void GetRoots_IncludesHomeAndPhysicalDrive()
    {
        var roots = CreateService().GetRoots();

        Assert.NotEmpty(roots);
        Assert.Contains(roots, r => r.DisplayName == "Home");
        Assert.Contains(roots, r => Directory.Exists(r.Path));
    }

    [Fact]
    public void ListDirectory_DirectoryEntriesFirstThenFilesSorted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zeta"));
        File.WriteAllText(Path.Combine(_root, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "beta.txt"), "bb");

        var entries = CreateService().ListDirectory(_root);

        Assert.Equal(3, entries.Count);
        Assert.Equal("zeta", entries[0].Name);
        Assert.Equal("directory", entries[0].Kind);
        Assert.Null(entries[0].SizeBytes);
        Assert.Equal("alpha.txt", entries[1].Name);
        Assert.Equal("file", entries[1].Kind);
        Assert.Equal(1, entries[1].SizeBytes);
        Assert.Equal("beta.txt", entries[2].Name);
    }

    [Fact]
    public void ListDirectory_OnMissingPath_Throws()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        Assert.Throws<ArgumentException>(() => CreateService().ListDirectory(missing));
    }

    [Fact]
    public void ListDirectory_OnFile_Throws()
    {
        var file = Path.Combine(_root, "file.txt");
        File.WriteAllText(file, "x");
        Assert.Throws<ArgumentException>(() => CreateService().ListDirectory(file));
    }

    [Fact]
    public void ListDirectory_RelativePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService().ListDirectory("relative"));
    }

    [Fact]
    public void GetStat_Directory_ReportsDirectoryExists()
    {
        var stat = CreateService().GetStat(_root);
        Assert.True(stat.Exists);
        Assert.Equal("directory", stat.Kind);
        Assert.False(stat.IsGitRepository);
    }

    [Fact]
    public void GetStat_File_ReportsFileExists()
    {
        var file = Path.Combine(_root, "file.txt");
        File.WriteAllText(file, "x");
        var stat = CreateService().GetStat(file);
        Assert.True(stat.Exists);
        Assert.Equal("file", stat.Kind);
    }

    [Fact]
    public void GetStat_Missing_ReportsNotExists()
    {
        var stat = CreateService().GetStat(Path.Combine(_root, "missing"));
        Assert.False(stat.Exists);
        Assert.Equal("missing", stat.Kind);
    }

    [Fact]
    public void GetStat_GitRepository_Detected()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var stat = CreateService().GetStat(repo);
        Assert.True(stat.IsGitRepository);
        Assert.Equal("repo", stat.Name);
    }

    [Fact]
    public void CreateDirectory_CreatesAndReturnsStat()
    {
        var created = Path.Combine(_root, "new-dir");
        var stat = CreateService().CreateDirectory(created);

        Assert.True(Directory.Exists(created));
        Assert.True(stat.Exists);
        Assert.Equal("directory", stat.Kind);
    }

    [Fact]
    public void CreateDirectory_Existing_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => CreateService().CreateDirectory(_root));
    }

    [Fact]
    public void RevealInExplorer_ExistingDirectory_LaunchesExplorerWithFullPath()
    {
        var launcher = new FakeProcessLauncher();
        var service = new FileSystemService(launcher);

        service.RevealInExplorer(_root);

        Assert.Equal(1, launcher.CallCount);
        Assert.Equal("explorer.exe", launcher.LastFileName);
        Assert.Equal(Path.GetFullPath(_root), launcher.LastArgument);
    }

    [Fact]
    public void RevealInExplorer_MissingDirectory_ThrowsAndDoesNotLaunch()
    {
        var launcher = new FakeProcessLauncher();
        var service = new FileSystemService(launcher);
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Throws<ArgumentException>(() => service.RevealInExplorer(missing));
        Assert.Equal(0, launcher.CallCount);
    }
}