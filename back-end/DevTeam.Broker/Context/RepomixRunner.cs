using System.Text;

using DevTeam.Broker.Gates;

using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Context;

/// <summary>
/// Runs the Repomix CLI through <see cref="IProcessRunner"/> (the app's single process seam), so
/// this class is fakeable in tests and every invocation lands in the same process log as test
/// runs and builds. Never throws for a process-level failure: it classifies the result so the
/// caller can keep the old index and carry on (spec REQ-007).
/// </summary>
public sealed class RepomixRunner : IRepomixRunner
{
    private readonly IProcessRunner _runner;
    private readonly string _command;
    private readonly ILogger<RepomixRunner>? _logger;

    public RepomixRunner(IProcessRunner runner, string command, ILogger<RepomixRunner>? logger = null)
    {
        _runner = runner;
        _command = command;
        _logger = logger;
    }

    public async Task<RepomixResult> RunAsync(
        string workspacePath,
        string outputPath,
        bool compress,
        IReadOnlyList<string> ignore,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var (fileName, arguments) = BuildInvocation(workspacePath, outputPath, compress, ignore);

        ProcessRunResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessRunRequest(fileName, arguments, workspacePath, (int)timeout.TotalMilliseconds),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Repomix could not be launched for {Workspace}", workspacePath);
            return new RepomixResult(RepomixOutcome.Missing, outputPath, 0, string.Empty, ex.Message);
        }

        var outcome = Classify(result, outputPath);
        var bytes = outcome == RepomixOutcome.Success ? SafeLength(outputPath) : 0;
        return new RepomixResult(outcome, outputPath, bytes, result.StandardOutput, result.StandardError);
    }

    /// <summary>
    /// Resolves how to actually launch Repomix. On Windows an npm-installed CLI is a <c>.cmd</c>
    /// shim, which <see cref="System.Diagnostics.Process"/> with <c>UseShellExecute=false</c>
    /// cannot execute by bare name — so it is run through <c>cmd.exe /c</c>, which resolves it
    /// via PATHEXT (spec §12.3). Everything is quoted because workspace paths contain spaces.
    /// </summary>
    public (string FileName, string Arguments) BuildInvocation(
        string workspacePath, string outputPath, bool compress, IReadOnlyList<string> ignore)
    {
        var command = new StringBuilder(_command);
        command.Append(" --style xml");
        if (compress)
            command.Append(" --compress");
        command.Append(" --output ").Append(Quote(outputPath));
        if (ignore.Count > 0)
            command.Append(" --ignore ").Append(Quote(string.Join(',', ignore)));

        var line = command.ToString();

        if (OperatingSystem.IsWindows() && !_command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return ("cmd.exe", "/c " + line);

        return (_command, line[_command.Length..]);
    }

    private static RepomixOutcome Classify(ProcessRunResult result, string outputPath)
    {
        if (result.TimedOut)
            return RepomixOutcome.TimedOut;
        if (result.ExitCode == 0 && File.Exists(outputPath))
            return RepomixOutcome.Success;
        if (LooksMissing(result))
            return RepomixOutcome.Missing;
        return RepomixOutcome.Failed;
    }

    // A missing tool surfaces differently depending on how it was launched: SystemProcessRunner
    // reports "Process failed to start." for a bare name, while `cmd.exe /c` reports exit 9009
    // and "is not recognized". Both mean the same thing to us.
    private static bool LooksMissing(ProcessRunResult result)
    {
        if (result.ExitCode is 9009 or 127)
            return true;
        var text = result.StandardError;
        return text.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed to start", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
