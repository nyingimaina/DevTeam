using System.ComponentModel;
using DevTeam.Shared;

namespace DevTeam.Broker.Rpc;

/// <summary>
/// Starts the agent process, and turns "Windows would not start it" into something the API can
/// answer with.
///
/// <para>
/// winget installs the CLI as a symbolic link in <c>%LOCALAPPDATA%\Microsoft\WinGet\Links</c>, and
/// Windows can refuse to create a process whose image path traverses a reparse point —
/// <c>Win32Exception</c> 448, "the path cannot be traversed because it contains an untrusted mount
/// point". Resolving the link first removes the reparse point from the path, and that is the first
/// thing tried.
/// </para>
///
/// <para>
/// It is not the only thing, on purpose. Link resolution is a filesystem call that can fail, and
/// when it did it failed <em>silently</em> — the resolver returned the link unchanged, so "resolution
/// broke" and "Windows refused the resolved path" produced identical symptoms. Offering every
/// spelling removes the dependency on resolution being right, and if none can be started the failure
/// becomes an <see cref="AgentLaunchException"/> naming every path attempted, which
/// <see cref="ApiErrorMapper"/> turns into a 503 with an actionable message.
/// </para>
/// </summary>
public static class AcpProcessLaunch
{
    /// <summary>
    /// ERROR_UNTRUSTED_MOUNT_POINT. The only start failure worth trying another path for: it means
    /// "this spelling of the path is the problem", which is exactly what a second candidate fixes.
    /// Anything else (access denied, bad image) is unaffected by spelling, so retrying would only
    /// hide the real error.
    /// </summary>
    private const int ErrorUntrustedMountPoint = 448;

    public static IAcpProcess Create(
        string discoveredPath,
        IReadOnlyList<string> args,
        Action<string>? onStderr = null,
        Func<string, IReadOnlyList<string>, Action<string>?, IAcpProcess>? launch = null,
        string? localAppData = null) =>
        CreateWith(discoveredPath, args, onStderr, launch ?? DefaultLaunch, localAppData);

    private static IAcpProcess DefaultLaunch(string executable, IReadOnlyList<string> args, Action<string>? onStderr) =>
        OpencodeAcpProcess.Create(executable, args, onStderr);

    private static IAcpProcess CreateWith(
        string discoveredPath,
        IReadOnlyList<string> args,
        Action<string>? onStderr,
        Func<string, IReadOnlyList<string>, Action<string>?, IAcpProcess> launch,
        string? localAppData)
    {
        var candidates = OpenCodePathResolver.LaunchCandidates(discoveredPath, localAppData);
        Exception? last = null;

        foreach (var candidate in candidates)
        {
            try
            {
                return launch(candidate, args, onStderr);
            }
            catch (Win32Exception ex)
            {
                last = ex;
                // Only the reparse-point refusal means "this spelling of the path is the problem".
                // Access denied or a bad image is not fixed by a different spelling, so stop and
                // report the real reason instead of burning the other candidate on it.
                if (ex.NativeErrorCode != ErrorUntrustedMountPoint)
                    break;
            }
        }

        throw new AgentLaunchException(
            $"could not start the opencode agent. Tried: {string.Join(" | ", candidates)}. {Explain(last)}",
            last ?? new InvalidOperationException("no launch candidate could be tried"));
    }

    /// <summary>
    /// The reason as one line, with the Windows error code when there is one — it is the difference
    /// between "reinstall the CLI" and "your antivirus ate it", and it is not guessable from a 503.
    /// </summary>
    private static string Explain(Exception? ex) => ex switch
    {
        Win32Exception win32 => $"Windows reported error {win32.NativeErrorCode}: {win32.Message}",
        null => "no launch candidate could be tried.",
        _ => ex.Message,
    };
}
