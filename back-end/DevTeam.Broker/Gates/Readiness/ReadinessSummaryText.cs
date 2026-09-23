namespace DevTeam.Broker.Gates.Readiness;

/// <summary>
/// Turns a <see cref="ReadinessReport"/> into the plain sentences a non-technical person reads
/// in the checks list — no exit codes, no command names. Titles and reasons already come from
/// the profile in plain language; this only frames them.
/// </summary>
public static class ReadinessSummaryText
{
    public static string Describe(ReadinessReport report)
    {
        if (report.Phases.Count == 0)
            return "There was nothing to check in this project.";

        var lines = new List<string>();
        var failures = report.Failures;
        var skipped = report.Phases.Where(p => p.Status == ReadinessCheckStatus.Skipped).ToList();

        if (failures.Count == 0)
        {
            lines.Add(report.Phases.Count == 1
                ? "The check passed."
                : $"All {report.Phases.Count} checks passed.");
        }
        else
        {
            lines.Add(failures.Count == 1
                ? "1 check didn't pass:"
                : $"{failures.Count} checks didn't pass:");
            lines.AddRange(failures.Select(f => $"- {f.Title}: {f.Reason}"));
        }

        lines.AddRange(skipped.Select(s => $"- {s.Title}: not checked ({s.Reason})"));

        return string.Join('\n', lines);
    }
}
