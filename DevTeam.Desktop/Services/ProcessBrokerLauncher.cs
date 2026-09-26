using System.Diagnostics;

namespace DevTeam.Desktop.Services;

/// <summary>Launches the broker as a hidden child process using <see cref="Process"/>.</summary>
public sealed class ProcessBrokerLauncher : IBrokerLauncher, IDisposable
{
    // REQ-6: a kill-on-close job object guarantees the broker dies with the shell.
    private readonly WindowsJobObject? _job = WindowsJobObject.TryCreate();

    public IBrokerProcess Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        _job?.Assign(process.Id);
        return new ProcessBrokerProcess(process);
    }

    public void Dispose() => _job?.Dispose();

    private sealed class ProcessBrokerProcess : IBrokerProcess
    {
        private readonly Process _process;

        public ProcessBrokerProcess(Process process) => _process = process;

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public void Kill()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

        public void Dispose() => _process.Dispose();
    }
}
