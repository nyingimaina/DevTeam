using System.Text;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// The housekeeping <c>.gitignore</c> DevTeam ensures exists at the workspace root. Keeping build
/// output, logs and temp files out of git is industry practice, and it also keeps them out of the
/// diffs/packs/gate-evidence DevTeam pays tokens to read. Merging is additive: existing user lines
/// are never removed or reordered.
/// </summary>
public static class GitIgnoreTemplate
{
    public static readonly string[] DefaultLines =
    [
        "# Build output",
        "bin/",
        "obj/",
        "dist/",
        "out/",
        "build/",
        "publish/",
        "artifacts/",
        "# Dependencies",
        "node_modules/",
        ".next/",
        "# Test output",
        "coverage/",
        "TestResults/",
        "# IDE / editor",
        ".vs/",
        ".idea/",
        "*.user",
        "*.suo",
        "*.cache",
        "# OS noise",
        ".DS_Store",
        "Thumbs.db",
        "# Logs and temp files",
        "*.log",
        "*.tmp",
        "*.temp",
        "# Secrets / certs",
        ".env",
        ".env.local",
        ".env.*.local",
        "*.pfx",
        "*.pem",
        "*.key",
    ];

    /// <summary>Default lines that aren't already present, ignoring comments and blank lines.</summary>
    public static IReadOnlyList<string> MissingLines(string? existingText)
    {
        var present = new HashSet<string>(
            SplitLines(existingText).Select(line => line.Trim()).Where(line => line.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        return DefaultLines.Where(line => !present.Contains(line.Trim())).ToArray();
    }

    /// <summary>Existing content plus whichever default lines are missing. Idempotent.</summary>
    public static string Merge(string? existingText)
    {
        var missing = MissingLines(existingText);
        if (missing.Count == 0)
            return existingText ?? string.Empty;

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingText))
        {
            builder.Append(existingText.TrimEnd('\r', '\n'));
            builder.Append('\n');
            builder.Append('\n');
            builder.Append("# Added by DevTeam");
            builder.Append('\n');
        }
        else
        {
            builder.Append("# Build output, dependencies and editor/OS noise are not source.\n");
            builder.Append("# Added by DevTeam\n");
        }

        foreach (var line in missing)
            builder.Append(line).Append('\n');

        return builder.ToString();
    }

    private static IEnumerable<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n');
}
