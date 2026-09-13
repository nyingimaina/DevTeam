using System.Text;
using System.Text.RegularExpressions;

namespace DevTeam.Broker.Gates;

public static class SliceAllowlist
{
    public static bool IsAllowed(string changePath, string featureKey, IReadOnlyList<string> sharedFiles, IReadOnlyList<string> codePathTemplates)
    {
        var normalized = Normalize(changePath);
        if (normalized.Length == 0)
            return false;

        if (StartsWithSegment(normalized, "devteam"))
            return true;

        foreach (var shared in sharedFiles)
        {
            if (string.Equals(normalized, Normalize(shared), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var template in codePathTemplates)
        {
            var concrete = Normalize(template).Replace("<F>", featureKey, StringComparison.OrdinalIgnoreCase);
            if (TemplatePattern(concrete).IsMatch(normalized))
                return true;
        }

        return false;
    }

    private static readonly Dictionary<string, Regex> PatternCache = new(StringComparer.OrdinalIgnoreCase);

    private static Regex TemplatePattern(string template)
    {
        lock (PatternCache)
        {
            if (PatternCache.TryGetValue(template, out var cached))
                return cached;

            var pattern = new StringBuilder("^");
            if (template.Contains("**", StringComparison.Ordinal))
            {
                var parts = template.Split(["**"], StringSplitOptions.None);
                for (var i = 0; i < parts.Length; i++)
                {
                    var part = i == 0 ? parts[i] : parts[i].TrimStart('/');
                    pattern.Append(Regex.Escape(part));
                    if (i < parts.Length - 1)
                        pattern.Append("(?:[^/]+/)*");
                }
            }
            else
            {
                pattern.Append(Regex.Escape(template));
            }

            pattern.Append("(?:/.*)?$");
            var regex = new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase);
            PatternCache[template] = regex;
            return regex;
        }
    }

    private static bool StartsWithSegment(string path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return true;
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        return Regex.Replace(normalized, @"/{2,}", "/");
    }
}