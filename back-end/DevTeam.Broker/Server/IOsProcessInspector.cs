namespace DevTeam.Broker.Server;

/// <summary>A point-in-time snapshot of one OS process, for workspace-cleanup matching.</summary>
public sealed record OsProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    string? CommandLine);

/// <summary>
/// Read access to the live OS process table, and the ability to kill one by id.
/// An interface so workspace-cleanup logic can be unit-tested without real processes.
/// </summary>
public interface IOsProcessInspector
{
    IReadOnlyList<OsProcessSnapshot> ListProcesses();

    /// <summary>Best-effort kill; returns false if the process no longer exists or couldn't be killed.</summary>
    bool TryKill(int processId, bool entireProcessTree);
}
