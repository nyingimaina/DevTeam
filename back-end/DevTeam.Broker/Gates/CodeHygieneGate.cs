using System.Text;
using System.Text.RegularExpressions;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class CodeHygieneGate : IGate
{
    private static readonly Regex[] BannedPatterns =
    [
        new(@"\b(TODO|FIXME|HACK|XXX)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"(?i)\b(password|passwd|pwd|api[_-]?key|secret|token)\b\s*[:=]\s*[""'][^""']+[""']"),
    ];

    private readonly IProcessRunner _runner;

    public CodeHygieneGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.CodeHygiene;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var diff = await _runner.RunAsync(
            new ProcessRunRequest("git", "diff --unified=0", request.WorkspacePath),
            cancellationToken);

        if (diff.ExitCode != 0)
            return GateResult.Fail("Failed to run git diff", diff.StandardError.Trim());

        var hits = ScanAddedLines(diff.StandardOutput);
        cancellationToken.ThrowIfCancellationRequested();

        if (hits.Count == 0)
            return GateResult.Pass("No code-hygiene issues in the change", "TODO/FIXME/secret scan across added lines: clean");

        var evidence = new StringBuilder();
        foreach (var hit in hits)
            evidence.Append("fail: ").Append(hit).AppendLine();
        return GateResult.Fail($"{hits.Count} code-hygiene issue(s) added", evidence.ToString().TrimEnd());
    }

    internal static IReadOnlyList<string> ScanAddedLines(string diffOutput)
    {
        var hits = new List<string>();
        var currentFile = "?";
        foreach (var line in diffOutput.Split('\n'))
        {
            var headerMatch = Regex.Match(line, @"^\+\+\+\s+b/(?<file>.+)$");
            if (headerMatch.Success)
            {
                currentFile = headerMatch.Groups["file"].Value;
                continue;
            }

            if (!line.StartsWith('+') || line.StartsWith("+++"))
                continue;

            var added = line[1..];
            foreach (var pattern in BannedPatterns)
            {
                var match = pattern.Match(added);
                if (match.Success)
                    hits.Add($"{currentFile}: {match.Value.Trim()}");
            }
        }

        return hits.Take(10).ToList();
    }
}