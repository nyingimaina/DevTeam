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
        if (!trimmed.StartsWith("##", StringComparison.Ordinal) || trimmed.StartsWith("###", StringComparison.Ordinal))
            return false;

        var rest = trimmed[2..].TrimStart(' ', '\t');
        if (rest.Length == 0)
            return false;

        var colon = rest.IndexOf(':');
        if (colon >= 0)
        {
            id = rest[..colon].Trim();
            title = rest[(colon + 1)..].Trim();
            return id.Length > 0;
        }

        // Tolerate "## REQ-1 Title" without a colon, but only when the first token looks like an id
        // (letters and digits) so prose headings like "## Overview" are not misread as requirements.
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
}