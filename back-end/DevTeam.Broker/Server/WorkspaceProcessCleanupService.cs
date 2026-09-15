using DevTeam.Broker.Rpc;

namespace DevTeam.Broker.Server;

public sealed record StoppedProcessDto(int ProcessId, string Name);

public interface IWorkspaceProcessCleanupService
{
    IReadOnlyList<StoppedProcessDto> CleanupWorkspace(string workspacePath);
}

/// <summary>
/// Sweeps the OS process table for anything rooted inside a workspace folder — a
/// dev server the agent started and left running (npm, dotnet run, etc.) — and kills
/// it. Never touches the broker's own process or the shared opencode agent process,
/// even if either happens to path-match, since opencode is shared across every
/// workspace, not owned by one.
///
/// Exclusion is by exact process id only — deliberately not by ancestry. A
/// descendant of opencode (npm -> node, dotnet run, ...) must still be killed when
/// it matches on its own path/command-line evidence; walking the parent chain to
/// exclude it just because opencode is somewhere in its ancestry would defeat the
/// point of this sweep.
/// </summary>
public sealed class WorkspaceProcessCleanupService : IWorkspaceProcessCleanupService
{
    private readonly IOsProcessInspector _inspector;
    private readonly IAcpProcess _acpProcess;
    private readonly Func<int> _getBrokerProcessId;

    public WorkspaceProcessCleanupService(IOsProcessInspector inspector, IAcpProcess acpProcess)
        : this(inspector, acpProcess, () => Environment.ProcessId)
    {
    }

    public WorkspaceProcessCleanupService(IOsProcessInspector inspector, IAcpProcess acpProcess, Func<int> getBrokerProcessId)
    {
        _inspector = inspector;
        _acpProcess = acpProcess;
        _getBrokerProcessId = getBrokerProcessId;
    }

    public IReadOnlyList<StoppedProcessDto> CleanupWorkspace(string workspacePath)
    {
        var brokerPid = _getBrokerProcessId();
        var opencodePid = _acpProcess.ProcessId;
        var processes = _inspector.ListProcesses();

        var stopped = new List<StoppedProcessDto>();
        foreach (var process in processes)
        {
            if (process.ProcessId == brokerPid || process.ProcessId == opencodePid)
                continue;
            if (!IsRootedInWorkspace(workspacePath, process))
                continue;

            if (_inspector.TryKill(process.ProcessId, entireProcessTree: true))
                stopped.Add(new StoppedProcessDto(process.ProcessId, process.Name));
        }

        return stopped;
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
