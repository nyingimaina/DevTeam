using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Gates;

public sealed class SystemProcessRunner : IProcessRunner
{
    private const int OutputPreviewChars = 4000;

    private readonly ILogger<SystemProcessRunner>? _logger;

    public SystemProcessRunner(ILogger<SystemProcessRunner>? logger = null) => _logger = logger;

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        // Every external command the app runs goes through here (test runs, builds, linters,
        // git helpers), so this is the one place that makes "what did it actually execute, and
        // what did it say?" answerable after the fact.
        _logger?.LogDebug(
            "Process starting: {FileName} {Arguments} (cwd {WorkingDirectory}, timeoutMs {TimeoutMs})",
            request.FileName, request.Arguments, request.WorkingDirectory, request.TimeoutMs);

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
        {
            _logger?.LogError("Process {FileName} failed to start.", request.FileName);
            return new ProcessRunResult(-1, string.Empty, "Process failed to start.", false, TimeSpan.Zero);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var timeout = TimeSpan.FromMilliseconds(request.TimeoutMs);
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            var result = new ProcessRunResult(
                process.ExitCode,
                stdoutTask.Result,
                stderrTask.Result,
                false,
                DateTimeOffset.UtcNow - startedAt);

            LogFinished(request, result);
            return result;
        }
        catch (TimeoutException)
        {
            TryKill(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            _logger?.LogWarning(
                "Process TIMED OUT after {ElapsedMs}ms: {FileName} {Arguments} (cwd {WorkingDirectory})",
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, request.FileName, request.Arguments, request.WorkingDirectory);
            return new ProcessRunResult(-1, stdoutTask.Result, stderrTask.Result, true, DateTimeOffset.UtcNow - startedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKill(process);
            _logger?.LogError(ex, "Process failed to run: {FileName} {Arguments}", request.FileName, request.Arguments);
            return new ProcessRunResult(-1, string.Empty, ex.Message, false, DateTimeOffset.UtcNow - startedAt);
        }
    }

    private void LogFinished(ProcessRunRequest request, ProcessRunResult result)
    {
        if (_logger is null)
            return;

        if (result.ExitCode == 0)
        {
            _logger.LogDebug(
                "Process finished OK in {ElapsedMs}ms: {FileName} {Arguments} (stdout {StdoutChars} chars, stderr {StderrChars} chars)",
                result.Duration.TotalMilliseconds, request.FileName, request.Arguments,
                result.StandardOutput.Length, result.StandardError.Length);
            return;
        }

        // Non-zero is the interesting case: keep enough of the output to explain it.
        _logger.LogWarning(
            "Process exited {ExitCode} in {ElapsedMs}ms: {FileName} {Arguments} (cwd {WorkingDirectory})\n--- stderr ---\n{Stderr}\n--- stdout (tail) ---\n{Stdout}",
            result.ExitCode, result.Duration.TotalMilliseconds, request.FileName, request.Arguments, request.WorkingDirectory,
            Truncate(result.StandardError), Truncate(result.StandardOutput));
    }

    private static string Truncate(string text)
        => text.Length <= OutputPreviewChars ? text : text[^OutputPreviewChars..];

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