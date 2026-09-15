namespace DevTeam.Broker.Server;

/// <summary>
/// Shared path-containment check: is a candidate path inside a root directory?
/// A naive <c>StartsWith(root)</c> incorrectly matches a sibling directory that
/// merely shares a prefix (e.g. root "C:\work\proj" against candidate
/// "C:\work\proj2"), so this compares against the root with a trailing
/// separator (or exact equality) instead.
/// </summary>
public static class PathContainment
{
    public static bool IsWithinWorkspace(string workspaceRoot, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        try
        {
            var resolvedRoot = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var resolvedCandidate = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(Path.Combine(workspaceRoot, candidate));

            return resolvedCandidate.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase)
                || resolvedCandidate.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
