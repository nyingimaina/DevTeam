using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using Xunit;

namespace DevTeam.Tests;

public class WorkspaceProcessCleanupServiceTests
{
    private const int BrokerPid = 100;
    private const int OpencodePid = 200;
    private const int UnrelatedShellPid = 500; // a real dev-server's parent, e.g. a shell — not the broker or opencode

    private static WorkspaceProcessCleanupService CreateService(FakeOsProcessInspector inspector)
        => new(inspector, new FakeAcpProcessForCleanup(OpencodePid), () => BrokerPid);

    [Fact]
    public void CleanupWorkspace_NoMatches_ReturnsEmptyAndKillsNothing()
    {
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(300, BrokerPid, "unrelated.exe", @"C:\other\unrelated.exe", null));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Empty(result);
        Assert.Empty(inspector.KilledProcessIds);
    }

    [Fact]
    public void CleanupWorkspace_MatchViaExecutablePath_KillsAndReturnsIt()
    {
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            301, BrokerPid, "GamePlay.Api.exe", @"D:\apps\tictactoe\back-end\GamePlay.Api.exe", null));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Single(result);
        Assert.Equal(301, result[0].ProcessId);
        Assert.Equal("GamePlay.Api.exe", result[0].Name);
        Assert.Contains(301, inspector.KilledProcessIds);
    }

    [Fact]
    public void CleanupWorkspace_MatchViaCommandLineFallback_KillsAndReturnsIt()
    {
        // node.exe itself lives in a global install; only the command-line arg references the workspace.
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            302, BrokerPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\app\game-play\node_modules\next\dist\bin\next dev"));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Single(result);
        Assert.Equal(302, result[0].ProcessId);
        Assert.Contains(302, inspector.KilledProcessIds);
    }

    [Fact]
    public void CleanupWorkspace_SiblingPathDoesNotMatch()
    {
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            303, BrokerPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe2\front-end\node_modules\next\dist\bin\next dev"));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Empty(result);
        Assert.Empty(inspector.KilledProcessIds);
    }

    [Fact]
    public void CleanupWorkspace_NeverKillsTheBrokerItself()
    {
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            BrokerPid, 1, "DevTeam.Broker.exe", @"D:\apps\tictactoe\broker\DevTeam.Broker.exe", null));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Empty(result);
        Assert.Empty(inspector.KilledProcessIds);
    }

    [Fact]
    public void CleanupWorkspace_NeverKillsTheSharedOpencodeProcess()
    {
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            OpencodePid, BrokerPid, "opencode.exe", @"D:\apps\tictactoe\opencode.exe", null));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Empty(result);
        Assert.Empty(inspector.KilledProcessIds);
    }

    [Fact]
    public void CleanupWorkspace_DescendantOfOpencode_IsStillKilledWhenItMatchesOnItsOwnMerits()
    {
        // Exclusion is by exact process id only, not ancestry: a real descendant of
        // opencode (npm -> node) that matches on its own command line/path must
        // still be killed, even though opencode is somewhere in its ancestry.
        // An intermediate wrapper process with no workspace evidence of its own
        // (cmd.exe here) is simply never matched in the first place.
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            400, OpencodePid, "cmd.exe", @"C:\Windows\System32\cmd.exe", null)); // no workspace evidence -> not matched anyway
        inspector.Processes.Add(new OsProcessSnapshot(
            401, 400, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\node_modules\next\dist\bin\next dev")); // real descendant, matches on its own merits
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Single(result);
        Assert.Equal(401, result[0].ProcessId);
        Assert.Contains(401, inspector.KilledProcessIds);
    }

    [Fact]
    public void CleanupWorkspace_MultipleMatches_AllKilledAndReturned()
    {
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            301, BrokerPid, "GamePlay.Api.exe", @"D:\apps\tictactoe\back-end\GamePlay.Api.exe", null));
        inspector.Processes.Add(new OsProcessSnapshot(
            302, BrokerPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\node_modules\next\dist\bin\next dev"));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { 301, 302 }, inspector.KilledProcessIds.OrderBy(x => x));
    }

    [Fact]
    public void CleanupWorkspace_KillFailure_IsToleratedAndOmittedFromResult()
    {
        var inspector = new FakeOsProcessInspector { FailKillFor = [301] };
        inspector.Processes.Add(new OsProcessSnapshot(
            301, BrokerPid, "GamePlay.Api.exe", @"D:\apps\tictactoe\back-end\GamePlay.Api.exe", null));
        var service = CreateService(inspector);

        var result = service.CleanupWorkspace(@"D:\apps\tictactoe");

        Assert.Empty(result);
    }

    private sealed class FakeOsProcessInspector : IOsProcessInspector
    {
        public List<OsProcessSnapshot> Processes { get; } = [];
        public List<int> KilledProcessIds { get; } = [];
        public HashSet<int> FailKillFor { get; set; } = [];

        public IReadOnlyList<OsProcessSnapshot> ListProcesses() => Processes;

        public bool TryKill(int processId, bool entireProcessTree)
        {
            if (FailKillFor.Contains(processId))
                return false;
            KilledProcessIds.Add(processId);
            return true;
        }
    }

    private sealed class FakeAcpProcessForCleanup(int processId) : IAcpProcess
    {
        public int ProcessId => processId;
        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task WriteLineAsync(string line, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
