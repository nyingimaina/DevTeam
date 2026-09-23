using System.Text;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// The append-only record of the interactive Q&amp;A. The engine writes it turn by turn (not the
/// agent), so it survives a crash even if the BRS hasn't been written yet — and a fresh session
/// can be primed with it to continue instead of starting over.
/// </summary>
public static class InterviewLog
{
    public static int CountQuestions(string? existing)
    {
        if (string.IsNullOrEmpty(existing))
            return 0;

        return existing
            .Replace("\r\n", "\n")
            .Split('\n')
            .Count(line => line.StartsWith("## Q", StringComparison.Ordinal));
    }

    /// <summary>Appends one answered question. Idempotent only in the sense that it always adds a turn.</summary>
    public static string Append(string? existing, string question, string answer)
    {
        var builder = new StringBuilder(existing ?? string.Empty);
        if (builder.Length > 0 && !builder.ToString().EndsWith("\n\n", StringComparison.Ordinal))
            builder.Append('\n');

        var number = CountQuestions(existing) + 1;
        builder.Append("## Q").Append(number).AppendLine();
        builder.AppendLine(question.Trim());
        builder.AppendLine();
        builder.Append("**Answer:** ").AppendLine(string.IsNullOrWhiteSpace(answer) ? "(no answer)" : answer.Trim());
        builder.AppendLine();
        return builder.ToString();
    }
}
