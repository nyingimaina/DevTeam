using DevTeam.Broker.Rpc;

namespace DevTeam.Tests;

/// <summary>
/// npm/scoop/choco install opencode as a <c>.cmd</c>/<c>.bat</c> shim, which
/// <see cref="System.Diagnostics.Process"/> with <c>UseShellExecute=false</c> cannot start by name.
/// The shim therefore has to go through <c>cmd.exe</c>, and stdin/stdout must still be the ACP
/// channel rather than a console.
/// </summary>
[Collection(CompactionPolicyCollection.Name)]
public sealed class OpencodeAcpProcessLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devteam-launcher-" + Guid.NewGuid().ToString("N"));

    public OpencodeAcpProcessLauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void BuildShellCommand_QuotesAPathWithSpaces()
    {
        var command = OpencodeAcpProcess.BuildShellCommand(
            @"C:\Program Files\npm\opencode.cmd",
            ["acp"]);

        Assert.Equal("/d /s /c \"\"C:\\Program Files\\npm\\opencode.cmd\" acp\"", command);
    }

    [Fact]
    public void BuildShellCommand_LeavesASimplePathAlone()
    {
        var command = OpencodeAcpProcess.BuildShellCommand(
            @"C:\npm\opencode.cmd",
            ["acp"]);

        Assert.Equal("/d /s /c \"C:\\npm\\opencode.cmd acp\"", command);
    }

    [Fact]
    public void BuildShellCommand_PassesEveryArgument()
    {
        var command = OpencodeAcpProcess.BuildShellCommand(
            @"C:\npm\opencode.cmd",
            ["acp", "--foo"]);

        Assert.Equal("/d /s /c \"C:\\npm\\opencode.cmd acp --foo\"", command);
    }

    /// <summary>
    /// The real proof: a .cmd shim is launched and speaks over stdio exactly like an .exe would,
    /// which is what the ACP session depends on.
    /// </summary>
    [Fact]
    public async Task Create_ForACmdShim_StreamsStdioThroughTheShell()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var shim = Path.Combine(_root, "fake-opencode.cmd");
        File.WriteAllText(shim, "@echo off\r\necho HELLO_FROM_SHIM\r\n");

        using var process = OpencodeAcpProcess.Create(shim, ["acp"]);

        Assert.True(process.ProcessId > 0);

        var line = await process.ReadLineAsync(CancellationToken.None);
        Assert.Equal("HELLO_FROM_SHIM", line?.Trim());
    }

    [Fact]
    public async Task Create_ForACmdShim_AcceptsWritesToStdin()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var shim = Path.Combine(_root, "reads-stdin.cmd");
        File.WriteAllText(shim, "@echo off\r\nset /p line=\r\necho GOT:%line%\r\n");

        using var process = OpencodeAcpProcess.Create(shim, ["acp"]);

        await process.WriteLineAsync("{\"jsonrpc\":\"2.0\"}", CancellationToken.None);

        var line = await process.ReadLineAsync(CancellationToken.None);
        Assert.Equal("GOT:{\"jsonrpc\":\"2.0\"}", line?.Trim());
    }

    /// <summary>
    /// winget installs the CLI as a <em>symbolic link</em> in
    /// <c>%LOCALAPPDATA%\Microsoft\WinGet\Links</c>, so the resolved path traverses a reparse
    /// point. Windows can refuse process creation in that case (Win32Exception 448, "the path
    /// cannot be traversed because it contains an untrusted mount point"), which is what broke the
    /// broker in the wild. Launching goes through the link's final target.
    ///
    /// Note: this asserts the launch <em>works</em> through a link; it does not reproduce the
    /// refusal, which depends on the mitigation state of the launching process and could not be
    /// triggered from the test host. The resolver's link resolution itself is asserted directly in
    /// <see cref="OpenCodePathResolverTests.LaunchTarget_ResolvesASymbolicLinkToItsFinalTarget"/>.
    /// </summary>
    [Fact]
    public async Task Create_ThroughASymbolicLink_StillStreamsStdio()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var target = Path.Combine(_root, "linked-opencode.cmd");
        File.WriteAllText(target, "@echo off\r\necho HELLO_THROUGH_LINK\r\n");

        var link = Path.Combine(_root, "opencode-link.cmd");
        if (!TryCreateSymbolicLink(link, target))
            return; // Creating links needs Developer Mode or elevation; nothing to assert here.

        using var process = OpencodeAcpProcess.Create(link, ["acp"]);

        Assert.True(process.ProcessId > 0);

        var line = await process.ReadLineAsync(CancellationToken.None);
        Assert.Equal("HELLO_THROUGH_LINK", line?.Trim());
    }

    /// <summary>
    /// The real machine's winget link, when present. This is the exact path that failed in the
    /// wild, so it is worth asserting against rather than only against a synthetic link.
    /// </summary>
    [Fact]
    public async Task Create_ThroughTheWingetLink_StillStreamsStdio()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var link = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "opencode.exe");
        if (!File.Exists(link))
            return; // opencode was not installed by winget on this machine.
        if (new FileInfo(link).LinkTarget is null)
            return; // Not a link, so this test has nothing to add over the plain-path cases.

        using var process = OpencodeAcpProcess.Create(link, ["--version"]);

        Assert.True(process.ProcessId > 0);
    }

    /// <summary>
    /// The real proof that pruning reaches the agent: the launched child prints its own
    /// <c>OPENCODE_CONFIG_CONTENT</c> and the launcher is shown to have set it. Asserting only that
    /// <see cref="CompactionPolicy.Apply"/> edits a <see cref="ProcessStartInfo"/> would not catch the
    /// failure that actually matters - dropping the policy from a constructor chain would leave every
    /// unit test green while the agent silently ran with the default <c>prune=false</c>.
    ///
    /// Both launch paths are asserted because <see cref="OpencodeAcpProcess.Create"/> takes a
    /// different constructor for each: a <c>.cmd</c>/<c>.bat</c> shim goes through the raw-arguments
    /// constructor via <c>cmd.exe</c>, while a real <c>opencode.exe</c> - including the winget
    /// symbolic link - uses the argument-list constructor. Guarding only one of them would leave the
    /// other able to regress unnoticed.
    /// </summary>
    [Fact]
    public async Task Create_ForACmdShim_PassesTheCompactionPolicyToTheChildEnvironment()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var shim = Path.Combine(_root, "dumps-compaction.cmd");
        File.WriteAllText(shim, "@echo off\r\necho CONFIG:%OPENCODE_CONFIG_CONTENT%\r\n");

        var config = await ReadChildConfigAsync(() => OpencodeAcpProcess.Create(shim, ["acp"]));

        AssertCompactionPolicyReachesChild(config);
    }

    [Fact]
    public async Task Create_ForAnExecutable_PassesTheCompactionPolicyToTheChildEnvironment()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // cmd.exe stands in for opencode.exe, but through the argument-list constructor, which is the
        // path Create takes for a real executable and the one a real opencode.exe would use.
        var config = await ReadChildConfigAsync(() => new OpencodeAcpProcess(
            "cmd.exe",
            ["/d", "/s", "/c", "echo CONFIG:%OPENCODE_CONFIG_CONTENT%"]));

        AssertCompactionPolicyReachesChild(config);
    }

    private static async Task<string> ReadChildConfigAsync(Func<OpencodeAcpProcess> launch)
    {
        using var process = launch();
        var line = await process.ReadLineAsync(CancellationToken.None);
        return (line ?? string.Empty).Trim();
    }

    /// <summary>
    /// Asserts the child received the compaction policy, or the host's own explicit override.
    /// The host value is only read, never written: mutating the process-wide environment from a test
    /// races the other classes xUnit runs in parallel, and an earlier version of this test failed
    /// intermittently for exactly that reason.
    /// </summary>
    private static void AssertCompactionPolicyReachesChild(string config)
    {
        var inherited = Environment.GetEnvironmentVariable(CompactionPolicy.EnvironmentVariable);
        var expected = string.IsNullOrWhiteSpace(inherited) ? CompactionPolicy.ConfigContent : inherited;

        Assert.Equal("CONFIG:" + expected, config);
        Assert.Contains("\"prune\":true", CompactionPolicy.ConfigContent);
        Assert.Contains("\"reserved\":32768", CompactionPolicy.ConfigContent);
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
