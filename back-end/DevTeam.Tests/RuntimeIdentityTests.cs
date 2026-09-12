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
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData);

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
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData);

            Assert.Null(identity.OpenCodePath);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }
}