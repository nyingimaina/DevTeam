using DevTeam.Shared;

namespace DevTeam.Tests;

public class RuntimeIdentityTests
{
    [Fact]
    public void Resolve_NoOverrides_UsesDefaults()
    {
        var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(5202, identity.Port);
        Assert.Equal(@"C:\Users\tester\.devteam", identity.DataDirectory);
        Assert.Equal(@"C:\Users\tester\AppData\Local\DevTeam", identity.AppHomeDirectory);
        Assert.Equal("DevTeam.Desktop", identity.MutexName);
    }

    [Fact]
    public void Resolve_PortArg_WinsOverDefault()
    {
        var identity = RuntimeIdentity.Resolve(["--port=5999"], null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(5999, identity.Port);
    }

    [Fact]
    public void Resolve_PortEnv_UsedWhenNoArg()
    {
        var identity = RuntimeIdentity.Resolve([], "5888", null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(5888, identity.Port);
    }

    [Fact]
    public void Resolve_PortArg_WinsOverEnv()
    {
        var identity = RuntimeIdentity.Resolve(["--port=5777"], "5666", null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(5777, identity.Port);
    }

    [Theory]
    [InlineData("--port=0")]
    [InlineData("--port=foo")]
    [InlineData("--port=70000")]
    [InlineData("--port=-5")]
    public void Resolve_InvalidPort_FallsBackToDefault(string arg)
    {
        var identity = RuntimeIdentity.Resolve([arg], null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(5202, identity.Port);
    }

    [Fact]
    public void Resolve_DataDirArg_UsesFullPath()
    {
        var identity = RuntimeIdentity.Resolve(["--data-dir=C:\\work\\custom"], null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(@"C:\work\custom", identity.DataDirectory);
    }

    [Fact]
    public void Resolve_DataDirEnv_UsedWhenNoArg()
    {
        var identity = RuntimeIdentity.Resolve([], null, @"C:\work\fromenv", @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(@"C:\work\fromenv", identity.DataDirectory);
    }

    [Fact]
    public void Resolve_DataDirArg_WinsOverEnv()
    {
        var identity = RuntimeIdentity.Resolve(["--data-dir=C:\\work\\fromarg"], null, @"C:\work\fromenv", @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(@"C:\work\fromarg", identity.DataDirectory);
    }

    [Fact]
    public void DatabasePath_IsInsideDataDirectory()
    {
        var identity = RuntimeIdentity.Resolve(["--data-dir=C:\\work\\custom"], null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(@"C:\work\custom\devteam.db", identity.DatabasePath);
    }

    [Fact]
    public void LogsDirectory_IsInsideDataDirectory()
    {
        var identity = RuntimeIdentity.Resolve(["--data-dir=C:\\work\\custom"], null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");

        Assert.Equal(@"C:\work\custom\logs", identity.LogsDirectory);
    }

    [Fact]
    public void ReadArg_IgnoresCaseAndSplitsOnEquals()
    {
        Assert.Equal("123", RuntimeIdentity.ReadArg(["--PORT=123", "--port=456"], "--port"));
        Assert.Null(RuntimeIdentity.ReadArg(["--other=x"], "--port"));
        Assert.Equal("", RuntimeIdentity.ReadArg(["--port="], "--port"));
    }

    [Fact]
    public void Resolve_OpenCodeAtWinGetLinksPath_ReturnsThatPath()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var linksDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        Directory.CreateDirectory(linksDir);
        var exePath = Path.Combine(linksDir, "opencode.exe");
        File.WriteAllText(exePath, "stub");

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, openCodePathEnv: string.Empty);

            Assert.Equal(exePath, identity.OpenCodePath);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void Resolve_OpenCodeNotFoundAnywhere_ReturnsNull()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(localAppData);

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: string.Empty, openCodePathEnv: string.Empty);

            Assert.Null(identity.OpenCodePath);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    /// <summary>
    /// The CLI is installed by winget, scoop, choco, npm and the install script, and only some of
    /// those land in a directory we hardcode. Anything reachable on PATH must count, otherwise a
    /// machine with opencode installed reports that it is missing.
    /// </summary>
    [Fact]
    public void Resolve_OpenCodeOnPathOutsideKnownLocations_ReturnsThatPath()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var toolDir = Path.Combine(localAppData, "scoop-shims");
        Directory.CreateDirectory(toolDir);
        var exePath = Path.Combine(toolDir, "opencode.exe");
        File.WriteAllText(exePath, "stub");

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: toolDir, openCodePathEnv: string.Empty);

            Assert.Equal(exePath, identity.OpenCodePath);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void Resolve_OpenCodeEnvOverride_WinsOverEverythingElse()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var overrideDir = Path.Combine(localAppData, "explicit");
        var pathDir = Path.Combine(localAppData, "on-path");
        Directory.CreateDirectory(overrideDir);
        Directory.CreateDirectory(pathDir);
        var overrideExe = Path.Combine(overrideDir, "opencode.exe");
        File.WriteAllText(overrideExe, "stub");
        File.WriteAllText(Path.Combine(pathDir, "opencode.exe"), "stub");

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: pathDir, openCodePathEnv: overrideExe);

            Assert.Equal(overrideExe, identity.OpenCodePath);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void Resolve_OpenCodeEnvOverridePointingAtNothing_FallsBackToPath()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var pathDir = Path.Combine(localAppData, "on-path");
        Directory.CreateDirectory(pathDir);
        File.WriteAllText(Path.Combine(pathDir, "opencode.exe"), "stub");

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: pathDir, openCodePathEnv: Path.Combine(localAppData, "gone", "opencode.exe"));

            Assert.Equal(Path.Combine(pathDir, "opencode.exe"), identity.OpenCodePath);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    /// <summary>
    /// The desktop app is a separate product that ships no CLI, so it must not be mistaken for one.
    /// </summary>
    [Fact]
    public void DesktopAppOnly_ReportsNoCliButKnowsTheDesktopAppIsThere()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var desktopDir = Path.Combine(localAppData, "Programs", "opencode");
        Directory.CreateDirectory(desktopDir);
        File.WriteAllText(Path.Combine(desktopDir, "OpenCode.exe"), "stub");

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: string.Empty, openCodePathEnv: string.Empty);

            Assert.Null(identity.OpenCodePath);
            Assert.True(identity.OpenCodeDesktopAppInstalled);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    /// <summary>
    /// npm installs a <c>.cmd</c> shim, which <c>Process</c> cannot start directly, so the identity
    /// has to say that a shell is needed.
    /// </summary>
    [Fact]
    public void Resolve_OpenCodeCmdShim_ReportsThatAShellIsNeeded()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var npmDir = Path.Combine(localAppData, "npm");
        Directory.CreateDirectory(npmDir);
        var shim = Path.Combine(npmDir, "opencode.cmd");
        File.WriteAllText(shim, "stub");

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: npmDir, openCodePathEnv: string.Empty);

            Assert.Equal(shim, identity.OpenCodePath);
            Assert.True(identity.OpenCodeRequiresShell);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AnExe_ReportsThatNoShellIsNeeded()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var toolDir = Path.Combine(localAppData, "tool");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "opencode.exe"), "stub");

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: toolDir, openCodePathEnv: string.Empty);

            Assert.False(identity.OpenCodeRequiresShell);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void Resolve_RecordsTheDirectoriesItSearched()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var toolDir = Path.Combine(localAppData, "tool");
        Directory.CreateDirectory(toolDir);

        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: toolDir, openCodePathEnv: string.Empty);

            Assert.Contains(toolDir, identity.OpenCodeSearchedDirectories);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }
}