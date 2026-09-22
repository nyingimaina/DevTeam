using System.Text;
using System.Text.RegularExpressions;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Smoke-runs the built app so "the tests pass but the app doesn't start" is caught before a
/// human ever sees it. Tests exercise units; they can't catch a broken entry point (a WPF
/// <c>StartupUri</c> that resolves to nothing, a missing <c>Main</c>, a crash on launch) — this
/// starts the produced executable and fails if it dies immediately.
///
/// It is deliberately forgiving: if there's nothing to run it passes, and an app that is still
/// open when the smoke window elapses is a pass (a UI app that stays up is a working UI app).
/// </summary>
public sealed partial class AppLaunchGate : IGate
{
    public const int SmokeTimeoutMs = 8_000;

    private readonly IProcessRunner _runner;

    public AppLaunchGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.AppLaunch;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.WorkspacePath) || !Directory.Exists(request.WorkspacePath))
            return GateResult.Pass("Nothing to launch", "workspace folder not found");

        var (fileName, arguments) = ResolveCommand(request.WorkspacePath, request.FeatureKey);
        if (fileName is null)
            return GateResult.Pass("No runnable app to launch", "the workspace has no executable project");

        // Path.GetDirectoryName returns "" (not null) for a bare command like "dotnet" — the
        // manifest-runCommand case — so a plain `?? request.WorkspacePath` never falls back and
        // the process would launch with an empty working directory instead of the workspace root.
        var resolvedDirectory = Path.GetDirectoryName(fileName);
        var workingDirectory = string.IsNullOrEmpty(resolvedDirectory) ? request.WorkspacePath : resolvedDirectory;
        var result = await _runner.RunAsync(
            new ProcessRunRequest(fileName, arguments, workingDirectory, SmokeTimeoutMs),
            cancellationToken);

        // Still running when the window elapsed: the app opened and stayed up.
        if (result.TimedOut)
            return GateResult.Pass("The app started and stayed open", $"{Path.GetFileName(fileName)} launched and kept running");

        if (result.ExitCode == 0)
            return GateResult.Pass("The app started and exited cleanly", $"{Path.GetFileName(fileName)} exited 0");

        var evidence = new StringBuilder();
        evidence.Append(Path.GetFileName(fileName))
            .Append(" exited immediately with code 0x")
            .Append(((uint)result.ExitCode).ToString("X8"))
            .AppendLine(" — the app crashed or wouldn't start.");
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            evidence.AppendLine(result.StandardError.Length > 1500 ? result.StandardError[..1500] : result.StandardError);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            evidence.Append(result.StandardOutput.Length > 800 ? result.StandardOutput[..800] : result.StandardOutput);

        return GateResult.Fail("The app didn't start", evidence.ToString().TrimEnd());
    }

    private static (string? FileName, string Arguments) ResolveCommand(string workspacePath, string? featureKey)
    {
        var manifest = string.IsNullOrWhiteSpace(featureKey)
            ? null
            : SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));

        if (!string.IsNullOrWhiteSpace(manifest?.RunCommand))
        {
            var trimmed = manifest!.RunCommand!.Trim();
            var separator = trimmed.IndexOf(' ');
            return separator <= 0 ? (trimmed, string.Empty) : (trimmed[..separator], trimmed[(separator + 1)..]);
        }

        return (FindAppExecutable(workspacePath), string.Empty);
    }

    /// <summary>
    /// Finds the app's built executable: the project declaring <c>Exe</c>/<c>WinExe</c>, then its
    /// output <c>&lt;AssemblyName&gt;.exe</c>. Test projects are skipped.
    /// </summary>
    public static string? FindAppExecutable(string workspacePath)
    {
        if (!Directory.Exists(workspacePath))
            return null;

        foreach (var project in Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspacePath, project).Replace('\\', '/');
            if (ExcludedSegments.Any(relative.Split('/').Contains))
                continue;
            if (Path.GetFileNameWithoutExtension(project).EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
                continue;

            string text;
            try
            {
                text = File.ReadAllText(project);
            }
            catch (IOException)
            {
                continue;
            }

            if (!ExecutableOutputType().IsMatch(text) || text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase))
                continue;

            var assembly = AssemblyName().Match(text).Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(assembly))
                assembly = Path.GetFileNameWithoutExtension(project);

            var output = Directory.EnumerateFiles(Path.GetDirectoryName(project)!, assembly + ".exe", SearchOption.AllDirectories)
                .Where(candidate => !candidate.Replace('\\', '/').Contains("/ref/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (output is not null)
                return output;
        }

        return null;
    }

    private static readonly string[] ExcludedSegments = [".git", ".vs", "bin", "obj", "node_modules", "devteam"];

    [GeneratedRegex(@"<OutputType>\s*(Exe|WinExe)\s*</OutputType>", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableOutputType();

    [GeneratedRegex(@"<AssemblyName>\s*(?<name>[^<\s]+)\s*</AssemblyName>", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyName();
}
