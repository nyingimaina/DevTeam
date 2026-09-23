using System.Text;
using System.Text.RegularExpressions;

namespace DevTeam.Broker.Context;

/// <summary>The rendered map plus its size estimate and any warnings (never a failure).</summary>
public sealed record CodeMapDocument(string Markdown, int ApproxTokens, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes <c>CODEBASE_MAP.md</c> — the short, human-readable overview the agents read first.
/// This first version is built deterministically from the pack's file list and the structure
/// facts, so it costs nothing and cannot invent modules. Everything below the USER NOTES marker
/// is preserved verbatim across regenerations (spec REQ-009).
/// </summary>
public static partial class CodeMapWriter
{
    public const string UserNotesMarker = "<!-- USER NOTES (preserved on regeneration) -->";
    public const int MaxApproxTokens = 5000;

    public static CodeMapDocument Build(
        string commit,
        DateTimeOffset builtAt,
        IReadOnlyList<string> packFilePaths,
        string structureMarkdown,
        string? existingMap)
    {
        var warnings = new List<string>();
        var builder = new StringBuilder();

        var shortCommit = RepoContextStore.CommitDirName(commit);
        builder.AppendLine($"<!-- generated from commit {shortCommit} on {builtAt:yyyy-MM-dd} — verify against real files before relying on it -->");
        builder.AppendLine("# Codebase map");
        builder.AppendLine();

        builder.AppendLine("## What this project is");
        builder.AppendLine(Describe(packFilePaths));
        builder.AppendLine();

        builder.AppendLine("## Modules");
        AppendModules(builder, packFilePaths);
        builder.AppendLine();

        builder.AppendLine("## Shared code — REUSE THESE");
        AppendShared(builder, packFilePaths);
        builder.AppendLine();

        builder.AppendLine("## Conventions");
        builder.AppendLine("- Keep new code inside the module it belongs to (see the paths above).");
        builder.AppendLine("- Features extend the one shared project; never add a second project or re-declare a shared type.");
        builder.AppendLine("- Add tests alongside the code you change.");
        builder.AppendLine();

        builder.AppendLine("## Do / Don't");
        builder.AppendLine("- Do reuse the shared code listed above before writing anything new.");
        builder.AppendLine("- Don't duplicate an existing type/component under a new name.");
        builder.AppendLine();

        builder.AppendLine("## Business entities");
        builder.AppendLine(EntitiesSection(structureMarkdown));
        builder.AppendLine();

        builder.AppendLine("## Known sharp edges");
        builder.AppendLine("- (none recorded yet — add yours below)");
        builder.AppendLine();

        builder.AppendLine(UserNotesMarker);
        builder.AppendLine();

        var notes = SanitizeUserNotes(ExtractUserNotes(existingMap));
        if (notes.Length > 0)
        {
            builder.AppendLine(notes);
            builder.AppendLine();
        }

        var markdown = builder.ToString();
        var approxTokens = (markdown.Length + 3) / 4;
        if (approxTokens > MaxApproxTokens)
            warnings.Add($"map-too-large:{approxTokens}");

        return new CodeMapDocument(markdown, approxTokens, warnings);
    }

    /// <summary>Everything after the USER NOTES marker is the user's to keep — copied verbatim.</summary>
    public static string ExtractUserNotes(string? existingMap)
    {
        if (string.IsNullOrEmpty(existingMap))
            return string.Empty;

        var index = existingMap.IndexOf(UserNotesMarker, StringComparison.Ordinal);
        if (index < 0)
            return string.Empty;

        return existingMap[(index + UserNotesMarker.Length)..].Trim('\r', '\n');
    }

    /// <summary>
    /// The map is spliced into every future prompt, so user notes that look like instructions to
    /// the next agent are dropped rather than obeyed (§12.14).
    /// </summary>
    public static string SanitizeUserNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return string.Empty;

        var kept = notes
            .Split('\n')
            .Where(line => !InstructionLikeLine().IsMatch(line))
            .Select(line => line.TrimEnd('\r'));

        return string.Join('\n', kept).Trim('\n');
    }

    private static string Describe(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return "No files were included in the last overview. Add source, then refresh the overview.";

        var areas = TopLevelAreas(files);
        return $"This workspace holds {files.Count} indexed files across {areas.Count} top-level areas: " +
               $"{string.Join(", ", areas.Take(8).Select(a => $"`{a}`"))}{(areas.Count > 8 ? ", …" : string.Empty)}. " +
               "This summary is generated from the file layout, not from reading every file — verify against the real files when in doubt.";
    }

    private static void AppendModules(StringBuilder builder, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            builder.AppendLine("- (no files indexed)");
            return;
        }

        var groups = files
            .GroupBy(ModuleOf)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(25);

        foreach (var group in groups)
            builder.AppendLine($"- `{group.Key}` — {group.Count()} file(s)");
    }

    private static void AppendShared(StringBuilder builder, IReadOnlyList<string> files)
    {
        var shared = files
            .Where(path => path.Replace('\\', '/').Split('/')
                .Any(segment => segment.Equals("Core", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("Shared", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("Common", StringComparison.OrdinalIgnoreCase)))
            .Take(30)
            .ToList();

        if (shared.Count == 0)
        {
            builder.AppendLine("- (no shared folder detected — look for existing services/types before writing new ones)");
            return;
        }

        foreach (var path in shared)
            builder.AppendLine($"- `{path.Replace('\\', '/')}`");
    }

    private static string EntitiesSection(string structureMarkdown)
    {
        var lines = structureMarkdown.Replace("\r\n", "\n").Split('\n');
        var collecting = false;
        var collected = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (collecting)
                    break;
                collecting = line.Trim().Equals("## Entities", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (collecting && !string.IsNullOrWhiteSpace(line))
                collected.Add(line);
        }

        return collected.Count == 0 ? "- (none detected)" : string.Join('\n', collected);
    }

    private static string ModuleOf(string file)
    {
        var parts = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return ".";
        if (parts.Length == 1)
            return ".";

        // "back-end/src/Features/Login/X.cs" → "back-end/src" is more useful than "back-end",
        // and "front-end/app/Release" than "front-end". Group two levels when the first is a
        // conventional container folder.
        if (parts[0] is "back-end" or "front-end" or "src" or "app" && parts.Length > 2)
            return parts[0] + "/" + parts[1];

        return parts[0];
    }

    private static List<string> TopLevelAreas(IReadOnlyList<string> files)
        => files
            .Select(path => path.Replace('\\', '/').Split('/')[0])
            .Where(area => area.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex(@"^\s*(?:ignore (?:all )?previous|disregard|forget (?:all )?(?:previous|prior)|system\s*:|assistant\s*:)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InstructionLikeLine();
}
