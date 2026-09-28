namespace DevTeam.Broker.Gates;

/// <summary>
/// How to actually launch a command line. npm-installed CLIs are .cmd shims on Windows, which
/// Process with UseShellExecute=false cannot resolve by bare name - so they go through
/// <c>cmd.exe /c</c>, the same approach RepomixRunner and OpencodeAcpProcess use. Everything is
/// a single string on the wire, so paths containing spaces need quoting by the caller.
/// </summary>
public static class CommandInvocation
{
    public static (string FileName, string Arguments) ForNpmShim(string commandLine)
    {
        if (OperatingSystem.IsWindows() && !commandLine.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return ("cmd.exe", "/c " + commandLine);

        var separator = commandLine.IndexOf(' ');
        return separator <= 0
            ? (commandLine, string.Empty)
            : (commandLine[..separator], commandLine[(separator + 1)..]);
    }
}
