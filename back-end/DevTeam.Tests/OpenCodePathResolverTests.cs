using DevTeam.Shared;

namespace DevTeam.Tests;

/// <summary>
/// The resolver has to find opencode however it was installed. npm puts a <c>opencode.cmd</c>
/// shim in <c>%APPDATA%\npm</c> (the bin target is a real .exe inside the package), scoop and
/// choco use their own shims, and winget uses an .exe. A shell finds all of them through PATHEXT,
/// so anything that only looks for <c>opencode.exe</c> reports a working install as missing.
/// </summary>
public sealed class OpenCodePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devteam-resolver-" + Guid.NewGuid().ToString("N"));

    public OpenCodePathResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string NewDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string Write(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "stub");
        return path;
    }

    private string LocalAppData => Path.Combine(_root, "localappdata");

    [Fact]
    public void Resolve_FindsCmdShimOnPath()
    {
        var shimDir = NewDir("npm");
        var shim = Write(shimDir, "opencode.cmd");

        var probe = OpenCodePathResolver.Probe(LocalAppData, shimDir, explicitPath: null, programFiles: Path.Combine(_root, "pf"));

        Assert.Equal(shim, probe.Path);
        Assert.True(probe.RequiresShell);
    }

    [Fact]
    public void Resolve_FindsBatShimOnPath()
    {
        var shimDir = NewDir("choco");
        var shim = Write(shimDir, "opencode.bat");

        var probe = OpenCodePathResolver.Probe(LocalAppData, shimDir, null, Path.Combine(_root, "pf"));

        Assert.Equal(shim, probe.Path);
        Assert.True(probe.RequiresShell);
    }

    [Fact]
    public void Resolve_PrefersExeOverCmdInTheSameDirectory()
    {
        var dir = NewDir("both");
        var exe = Write(dir, "opencode.exe");
        Write(dir, "opencode.cmd");

        var probe = OpenCodePathResolver.Probe(LocalAppData, dir, null, Path.Combine(_root, "pf"));

        Assert.Equal(exe, probe.Path);
        Assert.False(probe.RequiresShell);
    }

    [Fact]
    public void Resolve_SearchesPathInOrder()
    {
        var first = NewDir("first");
        var second = NewDir("second");
        var expected = Write(first, "opencode.cmd");
        Write(second, "opencode.exe");

        var probe = OpenCodePathResolver.Probe(LocalAppData, first + ";" + second, null, Path.Combine(_root, "pf"));

        Assert.Equal(expected, probe.Path);
    }

    [Fact]
    public void Resolve_FindsCmdInKnownWinGetLocation()
    {
        var links = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links");
        Directory.CreateDirectory(links);
        var shim = Write(links, "opencode.cmd");

        var probe = OpenCodePathResolver.Probe(LocalAppData, pathEnv: string.Empty, null, Path.Combine(_root, "pf"));

        Assert.Equal(shim, probe.Path);
        Assert.True(probe.RequiresShell);
    }

    [Fact]
    public void Resolve_ExplicitPathWinsOverEverything()
    {
        var dir = NewDir("onpath");
        Write(dir, "opencode.exe");
        var explicitDir = NewDir("explicit");
        var explicitExe = Write(explicitDir, "opencode.exe");

        var probe = OpenCodePathResolver.Probe(LocalAppData, dir, explicitExe, Path.Combine(_root, "pf"));

        Assert.Equal(explicitExe, probe.Path);
    }

    /// <summary>
    /// The desktop app is a different product. Windows is case-insensitive, so its
    /// <c>OpenCode.exe</c> would otherwise be handed to a caller about to run <c>opencode acp</c>.
    /// </summary>
    [Fact]
    public void Resolve_DoesNotMistakeTheDesktopAppForTheCli()
    {
        var desktopDir = Path.Combine(LocalAppData, "Programs", "opencode");
        Directory.CreateDirectory(desktopDir);
        Write(desktopDir, "OpenCode.exe");

        var probe = OpenCodePathResolver.Probe(LocalAppData, pathEnv: string.Empty, null, Path.Combine(_root, "pf"));

        Assert.Null(probe.Path);
        Assert.True(OpenCodePathResolver.IsDesktopAppInstalled(LocalAppData));
    }

    [Fact]
    public void Probe_RecordsWhereItLooked()
    {
        var dir = NewDir("looked");

        var probe = OpenCodePathResolver.Probe(LocalAppData, dir, null, Path.Combine(_root, "pf"));

        Assert.Null(probe.Path);
        Assert.Contains(dir, probe.SearchedDirectories);
    }

    [Fact]
    public void Probe_FindsNothingWhenPathIsEmpty()
    {
        var probe = OpenCodePathResolver.Probe(LocalAppData, pathEnv: string.Empty, null, Path.Combine(_root, "pf"));

        Assert.Null(probe.Path);
        Assert.False(probe.RequiresShell);
    }

    [Theory]
    [InlineData("opencode.exe", false)]
    [InlineData("opencode.com", false)]
    [InlineData("opencode.cmd", true)]
    [InlineData("opencode.bat", true)]
    public void RequiresShell_IsDrivenByTheExtension(string fileName, bool expected)
    {
        Assert.Equal(expected, OpenCodePathResolver.RequiresShell(Path.Combine(_root, fileName)));
    }
}
