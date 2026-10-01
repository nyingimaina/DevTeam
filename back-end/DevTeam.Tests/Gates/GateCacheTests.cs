using System.Security.Cryptography;
using System.Text;

using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

/// <summary>
/// The reuse key is the whole contract: it must be equal exactly when a re-run would answer the
/// same, which means byte content and not a clock, an attempt number, or "nothing recent".
/// </summary>
public class GateCacheTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "devteam-gatecache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string relative, string content)
    {
        var full = Path.Combine(_workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full.Replace('\\', '/');
    }

    [Fact]
    public void SameContentSameKey()
    {
        Write("a.ts", "const a = 1;");
        var first = GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane");
        var second = GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane");

        Assert.Equal(first, second);
    }

    [Fact]
    public void EditingOneByteMisses()
    {
        Write("a.ts", "const a = 1;");
        var before = GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane");

        Write("a.ts", "const a = 2;");

        var after = GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane");
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ANewChangedFileMisses()
    {
        Write("a.ts", "const a = 1;");
        var before = GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane");

        Write("b.ts", "const b = 2;");

        var after = GateCache.ForChangedFiles(_workspace, ["a.ts", "b.ts"], "main", "fast_lane");
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ARemovedChangedFileMisses()
    {
        Write("a.ts", "const a = 1;");
        Write("b.ts", "const b = 2;");
        var before = GateCache.ForChangedFiles(_workspace, ["a.ts", "b.ts"], "main", "fast_lane");

        var after = GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void PathOrderDoesNotChangeTheKey()
    {
        // git reports changed paths in whatever order it likes; the same set must hash the same way
        // or every re-check misses for no reason.
        Write("a.ts", "1");
        Write("b.ts", "2");

        Assert.Equal(
            GateCache.ForChangedFiles(_workspace, ["a.ts", "b.ts"], "main", "fast_lane"),
            GateCache.ForChangedFiles(_workspace, ["b.ts", "a.ts"], "main", "fast_lane"));
    }

    [Fact]
    public void ADifferentBaseRefIsADifferentKey()
    {
        Write("a.ts", "1");

        Assert.NotEqual(
            GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane"),
            GateCache.ForChangedFiles(_workspace, ["a.ts"], "release", "fast_lane"));
    }

    [Fact]
    public void AChangedCommandIsADifferentKey()
    {
        Write("a.ts", "1");

        Assert.NotEqual(
            GateCache.ForWorkspace(_workspace, "npm test", _workspace, ["a.ts"]),
            GateCache.ForWorkspace(_workspace, "npm run test:unit", _workspace, ["a.ts"]));
    }

    [Fact]
    public void AChangedWorkingDirectoryIsADifferentKey()
    {
        Write("a.ts", "1");

        Assert.NotEqual(
            GateCache.ForWorkspace(_workspace, "npm test", _workspace, ["a.ts"]),
            GateCache.ForWorkspace(_workspace, "npm test", _workspace + "/front-end", ["a.ts"]));
    }

    [Fact]
    public void AMissingFileIsStableRatherThanThrowing()
    {
        // A gate that genuinely cannot answer must fail on its own evidence. If hashing threw, the
        // reuse lookup would crash the check instead of falling back to running it.
        var key = GateCache.ForChangedFiles(_workspace, ["does-not-exist.ts"], "main", "fast_lane");

        Assert.False(string.IsNullOrWhiteSpace(key));
    }

    [Fact]
    public void DifferentWorkspacesWithIdenticalNamesAreDifferentKeys()
    {
        Write("a.ts", "1");
        var other = Path.Combine(Path.GetTempPath(), "devteam-gatecache-other-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "a.ts"), "1");

            Assert.NotEqual(
                GateCache.ForChangedFiles(_workspace, ["a.ts"], "main", "fast_lane"),
                GateCache.ForChangedFiles(other, ["a.ts"], "main", "fast_lane"));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void TheKeyIsNotAPlainEchoOfThePaths()
    {
        // Guards against a "key" that just concatenates input and would collide on any separator
        // ambiguity; it must be a real hash.
        Write("a.tsx", "1");
        Write("b.ts", "2");

        Assert.NotEqual(
            GateCache.ForChangedFiles(_workspace, ["a.tsx", "b.ts"], "main", "fast_lane"),
            GateCache.ForChangedFiles(_workspace, ["a.ts", "x/b.ts"], "main", "fast_lane"));
    }

    [Fact]
    public void HashIsStableAcrossCalls()
    {
        // The key has to survive a process restart, so it cannot depend on anything in memory.
        Assert.Equal(GateCache.Hash("value"), GateCache.Hash("value"));
        Assert.NotEqual(GateCache.Hash("value"), GateCache.Hash("Value"));
    }
}
