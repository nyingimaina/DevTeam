namespace DevTeam.Broker.Gates;

/// <summary>
/// Reads the BRS contract file (devteam/features/&lt;featureKey&gt;/BRS.md) back into
/// structured requirements so requirement-dependent gates can validate the authored content.
/// The file is written by <see cref="ScaffoldSpecsGate"/> and edited by the business-analyst agent
/// during the interactive conversation; headings have the form <c>## REQ-1: Feature title</c> followed
/// by the Given/When/Then acceptance criteria.
/// </summary>
public static class RequirementsExtractor
{
    public static IReadOnlyList<RequirementDtos.Requirement> Extract(string workspacePath, string featureKey)
    {
        var path = ArtifactPaths.BrsPath(workspacePath, featureKey);
        if (!File.Exists(path))
            return [];

        var requirements = new List<RequirementDtos.Requirement>();
        var currentId = (string?)null;
        var currentTitle = string.Empty;
        var currentLines = new List<string>();

        void Flush()
        {
            if (currentId is null)
                return;
            requirements.Add(new RequirementDtos.Requirement(
                currentId, currentTitle, string.Join(Environment.NewLine, currentLines).Trim()));
            currentId = null;
            currentTitle = string.Empty;
            currentLines.Clear();
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.TrimEnd();
            if (TryMatchRequirementHeading(line, out var id, out var title))
            {
                Flush();
                currentId = id;
                currentTitle = title;
                continue;
            }

            if (currentId is not null)
                currentLines.Add(line);
        }

        Flush();
        return requirements;
    }

    private static bool TryMatchRequirementHeading(string line, out string id, out string title)
    {
        id = string.Empty;
        title = string.Empty;

        var trimmed = line.Trim();
        string rest;
        if (trimmed.StartsWith("##", StringComparison.Ordinal) && !trimmed.StartsWith("###", StringComparison.Ordinal))
        {
            rest = trimmed[2..].TrimStart(' ', '\t');
        }
        else if (LooksLikeBareRequirementId(trimmed))
        {
            // Lenient fallback, defense-in-depth: the agent occasionally drops the "##" markdown
            // marker but still gets the "REQ-<n>" id right — the id convention exists precisely
            // so a formatting slip like that doesn't silently lose the whole requirement (and the
            // progress bars / coverage gate that depend on every REQ being found).
            rest = trimmed;
        }
        else
        {
            return false;
        }

        if (rest.Length == 0)
            return false;

        var colon = rest.IndexOf(':');
        if (colon >= 0)
        {
            id = rest[..colon].Trim();
            title = rest[(colon + 1)..].Trim();
            return id.Length > 0;
        }

        // Tolerate "## REQ-1 Title" (or the bare "REQ-1 Title") without a colon, but only when
        // the first token looks like an id (letters and digits) so prose headings like
        // "## Overview" are not misread as requirements.
        var space = rest.IndexOf(' ');
        var candidate = space > 0 ? rest[..space] : rest;
        var looksLikeId = candidate.Length > 0
            && candidate.Any(char.IsDigit)
            && !candidate.Equals(candidate.ToLowerInvariant(), StringComparison.Ordinal);
        if (!looksLikeId)
            return false;

        id = candidate;
        title = space > 0 ? rest[(space + 1)..].Trim() : string.Empty;
        return true;
    }

    // Conservative on purpose: only the bare (no "##") fallback uses this, so it must actually
    // look like "REQ-<digits>" right at the start of the line, or ordinary prose ("Requirements
    // are listed below") would be misread as a requirement heading.
    private static bool LooksLikeBareRequirementId(string trimmed)
    {
        if (!trimmed.StartsWith("REQ-", StringComparison.OrdinalIgnoreCase))
            return false;

        var afterPrefix = trimmed[4..];
        var digitCount = 0;
        while (digitCount < afterPrefix.Length && char.IsDigit(afterPrefix[digitCount]))
            digitCount++;

        return digitCount > 0;
    }
}