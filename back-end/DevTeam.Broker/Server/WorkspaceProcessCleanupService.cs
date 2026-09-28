using DevTeam.Broker.Notifications;
using DevTeam.Broker.Rpc;

namespace DevTeam.Broker.Server;

public sealed record StoppedProcessDto(int ProcessId, string Name);

public interface IWorkspaceProcessCleanupService
{
    Task<CleanupResultDto> CleanupWorkspace(string workspacePath);

    /// <summary>Stops only pids surfaced by the previous sweep's RequiresApproval list.</summary>
    Task<IReadOnlyList<StoppedProcessDto>> StopApproved(IReadOnlyList<int> processIds);
}

/// <summary>
/// Post-measurement counsel before every stop, so the sweep dies with what it started. The sweep
/// sweeps the OS process table for anything rooted inside a workspace folder — a dev server a
/// team's agent started and left running — and its ONLY silent kills are processes DevTeam
/// launched: the broker's own subprocess tree, and non-root descendants of the shared opencode
/// agent subprocess. Processes with an identity their workspace path suggests but launch history
/// does not confirm — an OpenCode session the user is running in the same repo, another DevTeam
/// broker, an editor/IDE, or a stale dev server — are simply embedded here as needing approval,
/// never killed by path/command-line faith alone.<br/><br/>
///
/// Under the npm shim, <see cref="IAcpProcess.ProcessId"/> is a cmd.exe wrapper, so ownership of
/// the REAL agent process comes from the parent chain under the excluded pid — the wrapper's
/// non-agent descendants (npm/node dev servers) are still killed when they match the workspace,
/// while the agent roots inside that tree are not touched no matter where their executable sits.
///
/// Procedure robustness: a snapshot-to-kill pid reuse detection means each kill revalidates the
/// process identity; a changed machine state re-enters the approval list instead of killing an
/// unknown subprocess. The matcher never keys on the exact name "opencode" — identity is from
/// launch-time ancestry, not process-name substring matching.
/// </summary>
public sealed class WorkspaceProcessCleanupService : IWorkspaceProcessCleanupService
{
    private readonly IOsProcessInspector _inspector;
    private readonly IAcpProcess _acpProcess;
    private readonly Func<int> _getBrokerProcessId;
    private readonly IPlatformNotifier _notifier;
    private readonly object _pendingLock = new();
    private List<PendingStopDto> _lastPending = [];

    public WorkspaceProcessCleanupService(
        IOsProcessInspector inspector,
        IAcpProcess acpProcess,
        IPlatformNotifier notifier)
        : this(inspector, acpProcess, () => Environment.ProcessId, notifier)
    {
    }

    public WorkspaceProcessCleanupService(
        IOsProcessInspector inspector,
        IAcpProcess acpProcess,
        Func<int> getBrokerProcessId,
        IPlatformNotifier notifier)
    {
        _inspector = inspector;
        _acpProcess = acpProcess;
        _getBrokerProcessId = getBrokerProcessId;
        _notifier = notifier;
    }

    public async Task<CleanupResultDto> CleanupWorkspace(string workspacePath)
    {
        var brokerPid = _getBrokerProcessId();
        var acpPid = SafeAcpPid();
        var processes = _inspector.ListProcesses();
        var byPid = processes.ToDictionary(p => p.ProcessId);
        var descendantsOfAcp = SubtreeOf(acpPid, byPid);

        var stopped = new List<StoppedProcessDto>();
        var pending = new List<PendingStopDto>();

        foreach (var process in processes)
        {
            if (process.ProcessId == brokerPid || process.ProcessId == acpPid)
                continue; // our own substance, in either case.
            if (!IsRootedInWorkspace(workspacePath, process))
                continue;

            // Our shared agent's tree: the agent's own roots are untouchable, ever. Its
            // non-root descendants are unneeded work started by our agents — killable now.
            if (descendantsOfAcp.Contains(process.ProcessId))
            {
                if (IsOpencodeIdentity(process))
                    continue; // a second agent root under the wrapper: protected regardless of path.
                if (VerifyIdentity(process))
                {
                    if (_inspector.TryKill(process.ProcessId, entireProcessTree: true))
                        stopped.Add(new StoppedProcessDto(process.ProcessId, process.Name));
                    continue;
                }
                // identity mismatch — treat as unowned for classification instead.
            }
            else if (AncestryReachesParent(process.ProcessId, brokerPid, byPid))
            {
                // something the broker itself launched (runner subprocess, git worker, toast).
                if (VerifyIdentity(process))
                {
                    if (_inspector.TryKill(process.ProcessId, entireProcessTree: true))
                        stopped.Add(new StoppedProcessDto(process.ProcessId, process.Name));
                    continue;
                }
                // pid reuse: unknown what that pid is now — classify as unowned below.
            }

            // foreign (unowned) trees, or identity re-validation failed mid-flight
            pending.Add(ClassifyPending(process, byPid, workspacePath, brokerPid, acpPid));
        }

        lock (_pendingLock)
        {
            _lastPending = pending;
        }

        if (pending.Count > 0)
            await AskForApproval(workspacePath, pending);

        return new CleanupResultDto(stopped, pending);
    }

    public async Task<IReadOnlyList<StoppedProcessDto>> StopApproved(IReadOnlyList<int> processIds)
    {
        List<PendingStopDto> snapshot;
        lock (_pendingLock)
        {
            snapshot = _lastPending.ToList();
        }
        var valid = snapshot.Where(p => processIds.Contains(p.ProcessId)).ToList();
        var stopped = new List<StoppedProcessDto>();
        foreach (var pending in valid)
        {
            var fresh = _inspector.ListProcesses().FirstOrDefault(p => p.ProcessId == pending.ProcessId);
            if (fresh is null || !string.Equals(fresh.ExecutablePath, pending.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                continue; // no longer the process we asked about: never a kill decision by stale identity.
            if (_inspector.TryKill(pending.ProcessId, entireProcessTree: true))
                stopped.Add(new StoppedProcessDto(pending.ProcessId, fresh.Name));
        }
        lock (_pendingLock)
        {
            _lastPending = [.. _lastPending.Where(p => !stopped.Any(s => s.ProcessId == p.ProcessId))];
        }
        return stopped;
    }

    /// <summary>
    /// Ancestry containment for a process tree by parent chain walking in the
    /// snapshot — identity (whose child it is), not matching by name or path substring.
    /// </summary>
    private static HashSet<int> SubtreeOf(int rootPid, Dictionary<int, OsProcessSnapshot> byPid)
    {
        if (rootPid <= 0)
            return [];
        var result = new HashSet<int>();
        var children = byPid.Values.GroupBy(p => p.ParentProcessId).ToDictionary(g => g.Key, g => g.Select(p => p.ProcessId).ToList());
        var queue = new Queue<int>();
        foreach (var child in children.GetValueOrDefault(rootPid, []))
            queue.Enqueue(child);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!result.Add(current))
                continue; // avoid cycles
            foreach (var child in children.GetValueOrDefault(current, []))
                queue.Enqueue(child);
        }
        return result;
    }

    private static bool AncestryReachesParent(int processId, int ancestorPid, Dictionary<int, OsProcessSnapshot> byPid)
    {
        var seen = new HashSet<int>();
        var current = processId;
        while (byPid.TryGetValue(current, out var snapshot) && seen.Add(current))
        {
            if (snapshot.ParentProcessId == ancestorPid)
                return true;
            current = snapshot.ParentProcessId;
            if (current <= 0)
                return false;
        }
        return false;
    }

    private static bool IsOpencodeIdentity(OsProcessSnapshot process) =>
        Path.GetFileNameWithoutExtension(process.ExecutablePath ?? process.Name)
            .Equals("opencode", StringComparison.OrdinalIgnoreCase);

    private bool VerifyIdentity(OsProcessSnapshot snapshot)
    {
        // The table is a moving picture: a pid can be recycled between snapshot and kill. If the
        // re-read no longer shows the same executable, never kill on a stale match.
        if (_inspector.ListProcesses().FirstOrDefault(p => p.ProcessId == snapshot.ProcessId) is not { } fresh)
            return false;
        if (snapshot.ExecutablePath is not null && fresh.ExecutablePath is not null)
            return string.Equals(fresh.ExecutablePath, snapshot.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        return string.Equals(fresh.Name, snapshot.Name, StringComparison.OrdinalIgnoreCase);
    }

    // Approval list classification, in plain words — it names what got skipped and why.
    private static PendingStopDto ClassifyPending(
        OsProcessSnapshot process,
        Dictionary<int, OsProcessSnapshot> byPid,
        string workspacePath,
        int brokerPid,
        int acpPid)
    {
        if (AncestorRootMatches(process, byPid, IsDevTeamIdentity))
            return new PendingStopDto(process.ProcessId, process.Name, process.ExecutablePath ?? "",
                process.CommandLine,
                $"Another DevTeam instance (pid {RootPid(process, byPid)}) is running in this workspace.");

        if (AncestorRootMatches(process, byPid, IsOpencodeIdentity))
            return new PendingStopDto(process.ProcessId, process.Name, process.ExecutablePath ?? "",
                process.CommandLine,
                $"An OpenCode process DevTeam did not start (pid {RootPid(process, byPid)}).");

        return new PendingStopDto(process.ProcessId, process.Name, process.ExecutablePath ?? "",
            process.CommandLine, "A workspace process DevTeam did not start — stopped only with your approval.");
    }

    private static bool IsDevTeamIdentity(OsProcessSnapshot process) =>
        (process.Name ?? string.Empty).StartsWith("DevTeam.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The root process of this snapshot's tree whose identity matches the predicate.</summary>
    private static bool AncestorRootMatches(OsProcessSnapshot process, Dictionary<int, OsProcessSnapshot> byPid, Func<OsProcessSnapshot, bool> predicate)
    {
        var root = RootPid(process, byPid);
        return byPid.TryGetValue(root, out var rootSnapshot) && predicate(rootSnapshot);
    }

    private static int RootPid(OsProcessSnapshot process, Dictionary<int, OsProcessSnapshot> byPid)
    {
        var current = process;
        var seen = new HashSet<int>();
        while (byPid.TryGetValue(current.ParentProcessId, out var parent) && seen.Add(current.ParentProcessId))
            current = parent;
        return current.ProcessId;
    }

    private async Task AskForApproval(string workspacePath, List<PendingStopDto> pending)
    {
        var names = string.Join(", ", pending.Take(4).Select(p => p.Name));
        var suffix = pending.Count > 4 ? $" and {pending.Count - 4} more" : "";
        // Awaited (bounded): the ask must actually be raised before the sweep returns, or the
        // user could dismiss the UI notice and lose the only visible trail. A notifier that
        // cannot be reached — SemaNami offline, no toast helper — costs seconds, not the sweep.
        var notify = _notifier.NotifyAsync(
            new NotificationRequest(
                "DevTeam",
                $"{pending.Count} process(es) in {workspacePath} need your approval to stop: {names}{suffix}.",
                NotificationUrgency.Attention),
            CancellationToken.None);
        try
        {
            await Task.WhenAny(notify, Task.Delay(TimeSpan.FromSeconds(4)));
        }
        catch
        {
            // a notification that cannot be raised must never block a sweep result.
        }
    }

    private int SafeAcpPid()
    {
        try
        {
            return _acpProcess.ProcessId;
        }
        catch
        {
            // not launched yet, or already shut down: nothing to exclude by pid.
            return -1;
        }
    }

    private static bool IsRootedInWorkspace(string workspacePath, OsProcessSnapshot process)
    {
        if (PathContainment.IsWithinWorkspace(workspacePath, process.ExecutablePath))
            return true;

        return CommandLineReferencesWorkspace(workspacePath, process.CommandLine);
    }

    private static bool CommandLineReferencesWorkspace(string workspacePath, string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            return false;

        foreach (var token in commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim('"');
            if (candidate.Length >= 3 && candidate[1] == ':' && PathContainment.IsWithinWorkspace(workspacePath, candidate))
                return true;
        }

        return false;
    }
}
