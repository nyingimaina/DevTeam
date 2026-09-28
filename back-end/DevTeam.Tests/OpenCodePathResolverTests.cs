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

    [Fact]
    public void LaunchTarget_LeavesARegularFileAlone()
    {
        var path = Write(NewDir("plain"), "opencode.exe");

        Assert.Equal(path, OpenCodePathResolver.LaunchTarget(path));
    }

    [Fact]
    public void LaunchTarget_ResolvesASymbolicLinkToItsFinalTarget()
    {
        var target = Write(NewDir("realtarget"), "opencode.exe");
        var link = Path.Combine(NewDir("linkdir"), "opencode.exe");
        if (!TryCreateSymbolicLink(link, target))
            return; // Needs Developer Mode or elevation; nothing to assert without one.

        var resolved = OpenCodePathResolver.LaunchTarget(link);

        Assert.Equal(target, resolved, ignoreCase: OperatingSystem.IsWindows());
        Assert.NotEqual(link, resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaunchTarget_LeavesAMissingPathAlone()
    {
        var missing = Path.Combine(_root, "not-here", "opencode.exe");

        Assert.Equal(missing, OpenCodePathResolver.LaunchTarget(missing));
    }

    [Fact]
    public void LaunchCandidates_OffersTheRealBinary_WhenTheWingetLinkCannotBeResolved()
    {
        // The gap that produced the wild failure. winget's Links\opencode.exe is a symbolic link into
        // a versioned package folder, and the link is what Windows refused with error 448. Resolving
        // it is the obvious fix — but File.ResolveLinkTarget is itself a filesystem call that can
        // fail, and when it did the chain collapsed to a single candidate: the link, with nothing to
        // fall back to. So the real binary has to be findable without asking the link.
        var localAppData = NewDir("lad");
        var links = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        Directory.CreateDirectory(links);
        var link = Write(links, "opencode.exe"); // a plain file, i.e. unresolvable

        var realBinary = Path.Combine(
            localAppData, "Microsoft", "WinGet", "Packages",
            "SST.opencode_Microsoft.Winget.Source_8wekyb3d8bbwe", "opencode.exe");
        var packageDir = Path.GetDirectoryName(realBinary)!;
        Directory.CreateDirectory(packageDir);
        Write(packageDir, "opencode.exe");

        var candidates = OpenCodePathResolver.LaunchCandidates(link, localAppData);

        Assert.Contains(realBinary, candidates);
        Assert.Contains(link, candidates);
    }

    [Fact]
    public void LaunchCandidates_StartsWithTheResolvedTarget_SoTheSafePathIsTriedFirst()
    {
        var target = Write(NewDir("real"), "opencode.exe");
        var link = Path.Combine(NewDir("linkdir"), "opencode.exe");
        if (!TryCreateSymbolicLink(link, target))
            return; // Needs Developer Mode or elevation; nothing to assert without one.

        var candidates = OpenCodePathResolver.LaunchCandidates(link, LocalAppData);

        Assert.Equal(target, candidates[0], ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void LaunchCandidates_OffersOnlyThePath_WhenThereIsNoLinkAndNoWingetPackage()
    {
        var plain = Write(NewDir("solo"), "opencode.exe");

        var candidates = OpenCodePathResolver.LaunchCandidates(plain, NewDir("empty-lad"));

        Assert.Equal(new[] { plain }, candidates);
    }

    [Fact]
    public void LaunchCandidates_AreFreeOfDuplicates()
    {
        // A resolved target that is also the discovered path, plus a package copy: attempting the
        // same spelling twice only spends time failing identically.
        var real = Write(NewDir("pkg"), "opencode.exe");
        var localAppData = NewDir("lad2");
        var packageDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "SST.opencode_x");
        Directory.CreateDirectory(packageDir);
        Write(packageDir, "opencode.exe");

        var candidates = OpenCodePathResolver.LaunchCandidates(real, localAppData);

        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static bool TryCreateSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
