using System.Diagnostics;
using System.Text;

namespace DevTeam.Broker.Gates;

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        var started = process.Start();
        if (!started)
            return new ProcessRunResult(-1, string.Empty, "Process failed to start.", false, TimeSpan.Zero);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var timeout = TimeSpan.FromMilliseconds(request.TimeoutMs);
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessRunResult(
                process.ExitCode,
                stdoutTask.Result,
                stderrTask.Result,
                false,
                DateTimeOffset.UtcNow - startedAt);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessRunResult(-1, stdoutTask.Result, stderrTask.Result, true, DateTimeOffset.UtcNow - startedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKill(process);
            return new ProcessRunResult(-1, string.Empty, ex.Message, false, DateTimeOffset.UtcNow - startedAt);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}