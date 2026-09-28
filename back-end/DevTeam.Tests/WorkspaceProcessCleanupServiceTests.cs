using DevTeam.Broker.Notifications;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using Xunit;

namespace DevTeam.Tests;

// The sweep used to kill anything whose path or command line referenced the workspace, sparing
// only the broker's own pid and one ACP child pid — an evidence-only policy that could, and in a
// self-developing future would, terminate OpenCode processes DevTeam does not own (the user's own
// sessions — and the user's workers themselves being OpenCode). The policy is now ownership-first:
//
//   owned (in the broker's process tree, or the shared ACP agent's subtree, roots excluded)
//           → killed silently; that is what the sweep exists for.
//   foreign OpenCode / foreign DevTeam trees → ALWAYS surfaced for per-process approval first.
//   anything else workspace-rooted (stray dev servers included) → approval before a stop.
//
// Nothing may die in the gap between snapshot and kill: every kill revalidates the process
// identity first, and a changed pid tenant falls back to the approval list rather than the reaper.
public class WorkspaceProcessCleanupServiceTests
{
    private const int BrokerPid = 100;
    private const int OpencodePid = 200; // the shared ACP child (a cmd.exe wrapper under an npm shim)
    private const int UnrelatedShellPid = 500; // e.g. the user's terminal: foreign, not the broker or opencode

    [Fact]
    public async Task NoMatches_ReturnsEmptyAndKillsNothing()
    {
        var service = await SweepWith();
        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task UnownedWorkspaceRootedExe_IsListedForApproval_NotKilledOnItsOwn()
    {
        // A project opening must not stop anything the broker cannot attribute to itself, or
        // opening a project amounts to a kill order against whatever else lives in the repo.
        var service = await SweepWith(new OsProcessSnapshot(
            301, UnrelatedShellPid, "GamePlay.Api.exe", @"D:\apps\tictactoe\back-end\GamePlay.Api.exe", null));

        Assert.Empty(service.Result.Stopped);
        var pending = Assert.Single(service.Result.RequiresApproval);
        Assert.Equal(301, pending.ProcessId);
        Assert.Equal("GamePlay.Api.exe", pending.Name);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task UnownedNodeViaCommandLine_IsListedForApproval_NotKilled()
    {
        // node.exe lives in a global install; only the command-line arg references the workspace.
        var service = await SweepWith(new OsProcessSnapshot(
            302, UnrelatedShellPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\app\game-play\node_modules\next\dist\bin\next dev"));

        var pending = Assert.Single(service.Result.RequiresApproval);
        Assert.Equal(302, pending.ProcessId);
        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task BrokerOwnedChild_IsKilledSilently()
    {
        // Anything the broker itself launched (gate-runner subprocess, git worker, toast helper)
        // is owned by definition — the sweep's original purpose on an identity basis.
        var service = await SweepWith(new OsProcessSnapshot(
            303, BrokerPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\back-end\scripts\dev-server.js"));

        var stopped = Assert.Single(service.Result.Stopped);
        Assert.Equal(303, stopped.ProcessId);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Contains(303, service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task DescendantOfOurSharedOpencode_IsKilledSilently()
    {
        // Our agents start dev servers through the shared opencode process; their subprocesses
        // are utilities our own agents started — killable on the workspace evidence, no asking.
        var service = await SweepWith(new OsProcessSnapshot(
            400, OpencodePid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\node_modules\next\dist\bin\next dev"));

        var stopped = Assert.Single(service.Result.Stopped);
        Assert.Equal(400, stopped.ProcessId);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Contains(400, service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task OurOpencodeRealProcessUnderTheShellShim_IsProtected()
    {
        // Through the npm shim, `ProcessId` is the cmd.exe wrapper and the process powering
        // every turn is a descendant of it. It must not survive by luck of the wrapper's argv:
        // identity via the tree under the excluded pid protects it even when its executable
        // sits inside the workspace.
        var service = await SweepWith(new OsProcessSnapshot(
            450, OpencodePid, "node.exe", @"D:\apps\tictactoe\opencode-runtime\opencode", "node opencode acp"));

        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task TheSharedAcpChild_IsExcludedByItsOwnPid()
    {
        var service = await SweepWith(new OsProcessSnapshot(
            OpencodePid, BrokerPid, "cmd.exe", @"C:\Windows\System32\cmd.exe",
            @"cmd /d /s /c ""opencode.cmd acp"""));

        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task ForeignOpencodeProcess_InTheWorkspace_IsNeverKilledSilently()
    {
        // The exact incident the policy rewrite exists for: the user (or any other OpenCode
        // agent) runs in the same repo. A workspace match is not a death sentence — it becomes
        // a permission request, and only the user's approval can act on it.
        var service = await SweepWith(new OsProcessSnapshot(
            500, 1, "OpenCode.exe", @"D:\apps\tictactoe\.tools\OpenCode.exe", null));

        Assert.Empty(service.Result.Stopped);
        var pending = Assert.Single(service.Result.RequiresApproval);
        Assert.Equal(500, pending.ProcessId);
        Assert.Contains("OpenCode", pending.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task DescendantOfForeignOpencode_IsNeverKilledSilently()
    {
        // Collateral death by ancestry must not apply here either: a helper beneath someone
        // else's OpenCode session gets the same ask-first treatment as its root.
        var service = await SweepWith(
            new OsProcessSnapshot(501, 1, "OpenCode.exe", @"D:\apps\tictactoe\.tools\OpenCode.exe", null),
            new OsProcessSnapshot(502, 501, "node.exe", @"C:\nvm4w\nodejs\node.exe",
                @"node D:\apps\tictactoe\front-end\node_modules\watcher.js"));

        Assert.Empty(service.Result.Stopped);
        Assert.Equal(
            [501, 502],
            service.Result.RequiresApproval.Select(p => p.ProcessId).OrderBy(x => x));
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task AnotherDevTeamInstance_IsListedForApproval_NotKilled()
    {
        // A second broker is genuine interference — port contention, duplicate sweeps — but it
        // is another engine's substance, not ours to stop on its owner's behalf.
        var service = await SweepWith(new OsProcessSnapshot(
            600, 1, "DevTeam.Broker.exe", @"D:\apps\tictactoe\publish\broker\DevTeam.Broker.exe", null));

        Assert.Empty(service.Result.Stopped);
        var pending = Assert.Single(service.Result.RequiresApproval);
        Assert.Equal(600, pending.ProcessId);
        Assert.Contains("DevTeam", pending.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task SiblingPathStillDoesNotMatch()
    {
        var service = await SweepWith(new OsProcessSnapshot(
            303, UnrelatedShellPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe2\front-end\node_modules\next\dist\bin\next dev"));

        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task TheBrokerItself_IsNeverMatchedEvenInsideItsOwnWorkspace()
    {
        var service = await SweepWith(new OsProcessSnapshot(
            BrokerPid, 1, "DevTeam.Broker.exe", @"D:\apps\tictactoe\broker\DevTeam.Broker.exe", null));

        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task TheSharedAcpWrapperWithWorkspaceEvidence_IsNeverTouched()
    {
        var service = await SweepWith(new OsProcessSnapshot(
            OpencodePid, UnrelatedShellPid, "cmd.exe", @"D:\apps\tictactoe\tools\cmd.exe",
            @"cmd /d /s /c ""opencode.cmd acp"""));

        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Result.RequiresApproval);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task KillFailure_IsToleratedAndOmittedFromTheStoppedList()
    {
        var inspector = new FakeOsProcessInspector { FailKillFor = [303] };
        inspector.Processes.Add(new OsProcessSnapshot(
            303, BrokerPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\back-end\scripts\dev-server.js"));
        var service = await SweepWith(inspector);

        Assert.Empty(service.Result.Stopped);
    }

    [Fact]
    public async Task APidRecycledBetweenSnapshotAndKill_FallsBackToApproval()
    {
        // Between snapshot and kill the OS may hand the pid to a different process. Re-checking
        // identity first is what stops the sweep from killing an unknown tenant of the pid.
        var inspector = new FakeOsProcessInspector();
        inspector.Processes.Add(new OsProcessSnapshot(
            303, BrokerPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\back-end\scripts\dev-server.js"));
        inspector.TablesAfterSnapshot = [new OsProcessSnapshot(
            303, BrokerPid, "other.exe", @"C:\other\unrelated.exe", null)];
        var service = await SweepWith(inspector);

        Assert.Empty(service.Result.Stopped);
        Assert.Empty(service.Inspector.KilledProcessIds);
        var pending = Assert.Single(service.Result.RequiresApproval);
        Assert.Equal(303, pending.ProcessId);
    }

    [Fact]
    public async Task ApprovalWorkflow_UserMayApproveAProcessForStop()
    {
        var service = await SweepWith(new OsProcessSnapshot(
            901, UnrelatedShellPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\next dev"));
        var pending = Assert.Single(service.Result.RequiresApproval);
        Assert.Equal(901, pending.ProcessId);

        var stopped = await service.Service.StopApproved([901]);

        var stoppedDto = Assert.Single(stopped);
        Assert.Equal(901, stoppedDto.ProcessId);
        Assert.Contains(901, service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task ApprovalWorkflow_PidsNeverSeenInASweep_AreRejected()
    {
        var service = await SweepWith(new OsProcessSnapshot(
            901, UnrelatedShellPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\next dev"));

        // Unknown pids get nothing — approval is a decision on what was shown, not a kill-anyone voucher.
        var stopped = await service.Service.StopApproved([99999]);

        Assert.Empty(stopped);
        Assert.Empty(service.Inspector.KilledProcessIds);
    }

    [Fact]
    public async Task Notifier_IsAskedWhateverApprovalIsNeeded()
    {
        var notifier = new RecordingNotifier();
        var service = await SweepWith(notifier, new OsProcessSnapshot(
            901, UnrelatedShellPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\front-end\next dev"));

        var request = Assert.Single(notifier.Requests);
        Assert.Contains("approval", request.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NotificationUrgency.Attention, request.Urgency);
    }

    [Fact]
    public async Task Notifier_IsSilentWhenNothingNeedsApproval()
    {
        var notifier = new RecordingNotifier();
        var service = await SweepWith(notifier, new OsProcessSnapshot(
            303, BrokerPid, "node.exe", @"C:\nvm4w\nodejs\node.exe",
            @"node D:\apps\tictactoe\back-end\scripts\dev-server.js"));

        Assert.Empty(notifier.Requests);
    }

    private static async Task<(WorkspaceProcessCleanupService Service, FakeOsProcessInspector Inspector, CleanupResultDto Result)> SweepWith(
        params OsProcessSnapshot[] processes)
    {
        var inspector = new FakeOsProcessInspector();
        foreach (var p in processes)
            inspector.Processes.Add(p);
        return await SweepWithCore(inspector, new NotStoringNotifier());
    }

    private static Task<(WorkspaceProcessCleanupService Service, FakeOsProcessInspector Inspector, CleanupResultDto Result)> SweepWith(
        FakeOsProcessInspector inspector) => SweepWithCore(inspector, new NotStoringNotifier());

    private static async Task<(WorkspaceProcessCleanupService, FakeOsProcessInspector, CleanupResultDto)> SweepWith(
        IPlatformNotifier notifier, params OsProcessSnapshot[] processes)
    {
        var inspector = new FakeOsProcessInspector();
        foreach (var process in processes)
            inspector.Processes.Add(process);
        return await SweepWithCore(inspector, notifier);
    }

    private static async Task<(WorkspaceProcessCleanupService Service, FakeOsProcessInspector Inspector, CleanupResultDto Result)> SweepWithCore(
        FakeOsProcessInspector inspector, IPlatformNotifier notifier)
    {
        var service = new WorkspaceProcessCleanupService(
            inspector, new FakeAcpProcessForCleanup(OpencodePid), () => BrokerPid, notifier);
        var result = await service.CleanupWorkspace(@"D:\apps\tictactoe");
        return (service, inspector, result);
    }

    private sealed class FakeOsProcessInspector : IOsProcessInspector
    {
        public List<OsProcessSnapshot> Processes { get; } = [];

        /// <summary>
        /// Simulates the table changing between snapshot and kill: once set, every
        /// ListProcesses call after the first returns this instead, so a service-side
        /// revalidation can discover that the pid now belongs to someone else.
        /// </summary>
        public List<OsProcessSnapshot>? TablesAfterSnapshot { get; set; }

        public List<int> KilledProcessIds { get; } = [];
        public HashSet<int> FailKillFor { get; set; } = [];
        private int _listCalls;

        public IReadOnlyList<OsProcessSnapshot> ListProcesses()
        {
            var call = Interlocked.Increment(ref _listCalls);
            return call == 1 || TablesAfterSnapshot is null ? Processes : TablesAfterSnapshot;
        }

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

    private sealed class NotStoringNotifier : IPlatformNotifier
    {
        public Task NotifyAsync(NotificationRequest request, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingNotifier : IPlatformNotifier
    {
        public List<NotificationRequest> Requests { get; } = [];
        public Task NotifyAsync(NotificationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
