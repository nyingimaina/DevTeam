using DevTeam.Broker.Rpc;

namespace DevTeam.Tests;

/// <summary>
/// npm/scoop/choco install opencode as a <c>.cmd</c>/<c>.bat</c> shim, which
/// <see cref="System.Diagnostics.Process"/> with <c>UseShellExecute=false</c> cannot start by name.
/// The shim therefore has to go through <c>cmd.exe</c>, and stdin/stdout must still be the ACP
/// channel rather than a console.
/// </summary>
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
}
