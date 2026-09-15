using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace DevTeam.Broker.Server;

/// <summary>
/// Enumerates the live OS process table via WMI (<c>Win32_Process</c>), which — unlike
/// <see cref="Process.GetProcesses"/> — exposes each process's command line, needed to
/// recognize e.g. a globally-installed <c>node.exe</c> running a script inside a
/// specific workspace folder. Windows-only, matching the rest of this app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiProcessInspector : IOsProcessInspector
{
    public IReadOnlyList<OsProcessSnapshot> ListProcesses()
    {
        var results = new List<OsProcessSnapshot>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
        using var results0 = searcher.Get();
        foreach (ManagementBaseObject item in results0)
        {
            using (item)
            {
                results.Add(new OsProcessSnapshot(
                    ProcessId: Convert.ToInt32(item["ProcessId"]),
                    ParentProcessId: Convert.ToInt32(item["ParentProcessId"]),
                    Name: item["Name"] as string ?? string.Empty,
                    ExecutablePath: item["ExecutablePath"] as string,
                    CommandLine: item["CommandLine"] as string));
            }
        }

        return results;
    }

    public bool TryKill(int processId, bool entireProcessTree)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return false;

            process.Kill(entireProcessTree);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
