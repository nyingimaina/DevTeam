using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// A cheap "does it even compile" pre-check for the developer loop, run before the much slower
/// verify_code (full test run). A scaffold-mismatch or an agent edit that breaks the build shows
/// up here in seconds with a clear compiler error, instead of being buried inside verify_code's
/// test-output parsing several minutes later.
/// </summary>
public sealed class BuildCheckGate : IGate
{
    private readonly IProcessRunner _runner;

    public BuildCheckGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.BuildCheck;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var commandLine = GateInputs.GetOptional(request.Inputs, "buildCommand");
        if (commandLine is null)
        {
            var solution = FindFirst(request.WorkspacePath, "*.slnx") ?? FindFirst(request.WorkspacePath, "*.sln");
            if (solution is null)
                return GateResult.Pass("No solution file found to build-check", "nothing to check");
            commandLine = $"dotnet build \"{Path.GetFileName(solution)}\"";
        }

        var (fileName, arguments) = SplitCommand(commandLine);
        var result = await _runner.RunAsync(new ProcessRunRequest(fileName, arguments, request.WorkspacePath), cancellationToken);

        if (result.TimedOut)
            return GateResult.Fail("Timed out waiting for the build to finish.", "Build exceeded the time budget.");

        return result.ExitCode == 0
            ? GateResult.Pass("Build succeeded", string.Empty)
            : GateResult.Fail("Build failed", Tail(result.StandardOutput + "\n" + result.StandardError));
    }

    private static string Tail(string output)
        => output.Length <= 4000 ? output : "… earlier output trimmed …\n" + output[^4000..];

    private static string? FindFirst(string workspacePath, string pattern)
        => Directory.Exists(workspacePath)
            ? Directory.EnumerateFiles(workspacePath, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;

    private static (string FileName, string Arguments) SplitCommand(string commandLine)
    {
        var trimmed = commandLine.Trim();
        var separator = trimmed.IndexOf(' ');
        if (separator <= 0)
            return (trimmed, string.Empty);
        return (trimmed[..separator], trimmed[(separator + 1)..]);
    }
}
