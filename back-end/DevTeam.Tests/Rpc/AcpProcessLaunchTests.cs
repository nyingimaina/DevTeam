using System.ComponentModel;
using DevTeam.Broker.Rpc;
using Microsoft.AspNetCore.Http;

namespace DevTeam.Tests.Rpc;

/// <summary>
/// Why the agent is launched through a candidate chain instead of a single resolved path.
///
/// The wild failure was Win32Exception 448 — "the path cannot be traversed because it contains an
/// untrusted mount point" — raised because winget installs the CLI behind a symbolic link in
/// <c>WinGet\Links</c>. Resolving that link before <c>Process.Start</c> fixes it, but the resolver
/// is a filesystem call that can itself fail (and used to fail *silently*, returning the link
/// unchanged — so a broken resolution and a broken launch looked identical from the outside).
///
/// So the launch no longer trusts resolution: it tries the resolved target and the discovered path,
/// and if every candidate is refused it raises <see cref="AgentLaunchException"/> — which the
/// central <see cref="DevTeam.Broker.ApiErrorMapper"/> turns into a 503 with an actionable message
/// rather than an opaque 500.
/// </summary>
public sealed class AcpProcessLaunchTests
{
    /// <summary>ERROR_UNTRUSTED_MOUNT_POINT: the reparse-point refusal this whole path exists for.</summary>
    private const int ErrorUntrustedMountPoint = 448;

    [Fact]
    public void LaunchesTheResolvedTarget_WhenTheDiscoveredPathIsALink()
    {
        var link = MakeSymlink("opencode.exe", out var target);
        var attempted = new List<string>();
        var stub = new RecordingLauncher(attempted);

        var process = AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch);

        Assert.NotNull(process);
        Assert.Equal(new[] { target }, attempted);
    }

    [Fact]
    public void FallsBackToTheDiscoveredPath_WhenTheResolvedTargetIsRefused()
    {
        // The case a silent resolver produced: resolution "succeeded" into a path Windows still
        // refuses. Trying the other candidate recovers instead of surfacing a hard failure.
        var link = MakeSymlink("opencode.exe", out var target);
        var attempted = new List<string>();
        var stub = new RecordingLauncher(attempted, refuse: path => path == target);

        var process = AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch);

        Assert.NotNull(process);
        Assert.Equal(new[] { target, link }, attempted);
    }

    [Fact]
    public void RaisesAgentLaunchException_WhenNoCandidateCanBeStarted()
    {
        var link = MakeSymlink("opencode.exe", out var target);
        var stub = new RecordingLauncher(new List<string>(), refuse: _ => true);

        var error = Assert.Throws<AgentLaunchException>(
            () => AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, AgentLaunchStatus(error));
    }

    [Fact]
    public void TheFailure_NamesEveryPathTried_SoTheNextPersonCanSeeWhatWasAttempted()
    {
        var link = MakeSymlink("opencode.exe", out var target);
        var stub = new RecordingLauncher(new List<string>(), refuse: _ => true);

        var error = Assert.Throws<AgentLaunchException>(
            () => AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch));

        Assert.Contains(target, error.Message);
        Assert.Contains(link, error.Message);
    }

    [Fact]
    public void TheFailure_KeepsTheWindowsErrorCode_SoA448IsNotMistakenForSomethingElse()
    {
        var link = MakeSymlink("opencode.exe", out var target);
        var stub = new RecordingLauncher(new List<string>(), refuse: _ => true);

        var error = Assert.Throws<AgentLaunchException>(
            () => AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch));

        Assert.Contains(ErrorUntrustedMountPoint.ToString(), error.Message);
    }

    [Fact]
    public void TheFailure_KeepsTheOriginalException_AsTheInnerException()
    {
        var link = MakeSymlink("opencode.exe", out var target);
        var stub = new RecordingLauncher(new List<string>(), refuse: _ => true);

        var error = Assert.Throws<AgentLaunchException>(
            () => AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch));

        Assert.IsType<Win32Exception>(error.InnerException);
    }

    [Fact]
    public void ASingleCandidatePath_IsOnlyTriedOnce()
    {
        // Not a link, so there is nothing to fall back to; retrying the same path would just spend
        // time failing identically.
        var plain = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        var attempted = new List<string>();
        var stub = new RecordingLauncher(attempted, refuse: _ => true);

        Assert.Throws<AgentLaunchException>(() => AcpProcessLaunch.Create(plain, ["acp"], launch: stub.Launch));

        Assert.Equal(new[] { plain }, attempted);
    }

    [Fact]
    public void OnlyAStartRefusalTriggersTheFallback_NotAnArbitraryFailure()
    {
        // A permission problem or a corrupt image will not be fixed by launching a different
        // spelling of the same path, and retrying would hide the real error.
        var link = MakeSymlink("opencode.exe", out var target);
        var attempted = new List<string>();
        var stub = new RecordingLauncher(attempted, error: new Win32Exception(5, "Access is denied."));

        var error = Assert.Throws<AgentLaunchException>(
            () => AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch));

        Assert.Single(attempted);
        Assert.Contains("Access is denied", error.Message);
    }

    /// <summary>
    /// The mapping the API makes of a failed launch, asserted here so the launch layer and the API
    /// layer cannot drift apart over a status code.
    /// </summary>
    [Fact]
    public void FallsBackToTheRealBinary_WhenTheWingetLinkIsRefusedAndCannotBeResolved()
    {
        // The wild failure: resolution produced nothing, so the only candidate was the very symlink
        // Windows had just refused. Nothing was left to try, and the chat died with a 503. The
        // package folder holds the real binary, and it has to be reachable without the link.
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-lad-" + Guid.NewGuid().ToString("N"));
        var links = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        Directory.CreateDirectory(links);
        var link = Path.Combine(links, "opencode.exe");
        File.WriteAllText(link, "unresolvable shim");

        var realBinary = Path.Combine(
            localAppData, "Microsoft", "WinGet", "Packages", "SST.opencode_Microsoft.Winget.Source_x", "opencode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(realBinary)!);
        File.WriteAllText(realBinary, "real binary");

        var attempted = new List<string>();
        var stub = new RecordingLauncher(attempted, refuse: path => path == link);

        var process = AcpProcessLaunch.Create(link, ["acp"], launch: stub.Launch, localAppData: localAppData);

        Assert.NotNull(process);
        Assert.Equal(new[] { link, realBinary }, attempted);
    }

    private static int AgentLaunchStatus(AgentLaunchException error) =>
        DevTeam.Broker.ApiErrorMapper.Map(error)!.Value.StatusCode;
    private sealed class StubProcess : IAcpProcess
    {
        public bool Launched => true;

        public int ProcessId => 4242;

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task WriteLineAsync(string line, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLauncher(List<string> attempted, Func<string, bool>? refuse = null, Win32Exception? error = null)
    {
        public IAcpProcess Launch(string executable, IReadOnlyList<string> args, Action<string>? onStderr)
        {
            attempted.Add(executable);
            if (error is not null)
                throw error;
            if (refuse?.Invoke(executable) == true)
                throw new Win32Exception(ErrorUntrustedMountPoint, "The path cannot be traversed because it contains an untrusted mount point.");
            return new StubProcess();
        }
    }

    /// <summary>
    /// Creates a link whose target exists, and returns the link. The resolved target is therefore a
    /// real, absolute path — which is what a resolver hands back, and what the assertions compare
    /// against.
    /// </summary>
    private static string MakeSymlink(string name, out string target)
    {
        var dir = Path.Combine(Path.GetTempPath(), "devteam-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "real"));
        target = Path.Combine(dir, "real", name);
        File.WriteAllText(target, "not really an executable");
        var link = Path.Combine(dir, name);
        File.CreateSymbolicLink(link, target);
        return link;
    }
}
