using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class FileSystemLookupTests
{
    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "devteam-fslookup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void FindEntry_WithAnExactCaseMatch_ReturnsThePath()
    {
        var dir = CreateWorkspace();
        try
        {
            var expected = Path.Combine(dir, "codemap.json");
            File.WriteAllText(expected, "{}");

            var found = FileSystemLookup.FindEntry(dir, "codemap.json");

            Assert.Equal(expected, found);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindEntry_WithADifferentlyCasedMatch_StillFindsIt()
    {
        var dir = CreateWorkspace();
        try
        {
            var actual = Path.Combine(dir, "CodeMap.JSON");
            File.WriteAllText(actual, "{}");

            var found = FileSystemLookup.FindEntry(dir, "codemap.json");

            Assert.Equal(actual, found);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindEntry_WithNoMatch_ReturnsNull()
    {
        var dir = CreateWorkspace();
        try
        {
            Assert.Null(FileSystemLookup.FindEntry(dir, "codemap.json"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindEntry_WhenTheDirectoryDoesNotExist_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devteam-fslookup-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Null(FileSystemLookup.FindEntry(dir, "codemap.json"));
    }

    [Fact]
    public void FindEntry_FindsADirectoryEntryToo()
    {
        var dir = CreateWorkspace();
        try
        {
            var subdir = Path.Combine(dir, "Code-Map");
            Directory.CreateDirectory(subdir);

            var found = FileSystemLookup.FindEntry(dir, "code-map");

            Assert.Equal(subdir, found);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
