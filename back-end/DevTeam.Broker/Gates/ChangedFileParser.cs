namespace DevTeam.Broker.Gates;

/// <summary>
/// Parses the file paths reported by <c>git status --porcelain</c>, one per line, into a
/// normalized path. Handles the two rename/status formats git emits there:
/// (1) bare <c>?? path</c> / <c>M path</c> — the path is everything after the two status
/// columns; (2) <c>R  old -&gt; new</c> — the rename target is the file that actually exists,
/// so the right-hand side is what scope checks must run against.
/// </summary>
public static class ChangedFileParser
{
    public static IEnumerable<string> Parse(string porcelainOutput)
    {
        foreach (var rawLine in porcelainOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 4)
                continue;

            var path = line.Substring(3).Trim();
            if (path.Length == 0)
                continue;

            var separator = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (separator > 0)
                path = path[(separator + 4)..];

            yield return path.Replace('\\', '/');
        }
    }
}