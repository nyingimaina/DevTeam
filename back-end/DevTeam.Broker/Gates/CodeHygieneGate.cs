using System.Text;
using System.Text.RegularExpressions;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class CodeHygieneGate : IGate
{
    // TODO/FIXME/HACK/XXX only mean "leftover work marker" as a *comment*, written in the
    // ALL-CAPS convention IDEs recognize — not as any coincidental occurrence of the word
    // (e.g. a project named "Todo" producing `namespace Todo.Api`). The secret pattern is a
    // different, already-narrow signal (assignment-with-literal shape) and stays as-is.
    private static readonly (Regex Pattern, bool RequiresCommentContext)[] BannedPatterns =
    [
        (new(@"\b(TODO|FIXME|HACK|XXX)\b", RegexOptions.Compiled), true),
        (new(@"(?i)\b(password|passwd|pwd|api[_-]?key|secret|token)\b\s*[:=]\s*[""'][^""']+[""']"), false),
    ];

    private static readonly Dictionary<string, string[]> CommentTokensByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = ["//", "/*", "*"],
        [".ts"] = ["//", "/*", "*"],
        [".tsx"] = ["//", "/*", "*"],
        [".js"] = ["//", "/*", "*"],
        [".jsx"] = ["//", "/*", "*"],
        [".java"] = ["//", "/*", "*"],
        [".go"] = ["//", "/*", "*"],
        [".cpp"] = ["//", "/*", "*"],
        [".c"] = ["//", "/*", "*"],
        [".h"] = ["//", "/*", "*"],
        [".rs"] = ["//", "/*", "*"],
        [".css"] = ["/*", "*"],
        [".scss"] = ["//", "/*", "*"],
        [".py"] = ["#"],
        [".sh"] = ["#"],
        [".rb"] = ["#"],
        [".yml"] = ["#"],
        [".yaml"] = ["#"],
        [".toml"] = ["#"],
        [".html"] = ["<!--"],
        [".htm"] = ["<!--"],
        [".xml"] = ["<!--"],
        [".razor"] = ["<!--", "//", "@*"],
        [".cshtml"] = ["<!--"],
        [".md"] = ["<!--"],
        [".sql"] = ["--"],
    };

    private static readonly string[] FallbackCommentTokens = ["//", "#", "/*", "*", "<!--", "--"];

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
            foreach (var (pattern, requiresCommentContext) in BannedPatterns)
            {
                var match = pattern.Match(added);
                if (!match.Success) continue;
                if (requiresCommentContext && !HasCommentTokenBefore(added, match.Index, currentFile)) continue;
                hits.Add($"{currentFile}: {match.Value.Trim()}");
            }
        }

        return hits.Take(10).ToList();
    }

    private static bool HasCommentTokenBefore(string line, int matchIndex, string filePath)
    {
        var ext = Path.GetExtension(filePath);
        var tokens = CommentTokensByExtension.TryGetValue(ext, out var known) ? known : FallbackCommentTokens;
        var prefix = line[..matchIndex];
        var trimmedLine = line.TrimStart();

        foreach (var token in tokens)
        {
            if (token == "*")
            {
                if (trimmedLine.StartsWith('*')) return true;
                continue;
            }

            if (prefix.Contains(token, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}