using System.Text;
using System.Text.RegularExpressions;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// Heuristics for the "ask ONE question at a time" rule on interactive stages.
/// Counts question marks in the agent's visible reply while ignoring the
/// streamed tool-call echoes (e.g. "tool: execute" / "call_ab12cd34") that some
/// agents emit inline; the real tool calls are tracked separately as parts.
/// </summary>
public static partial class MultiQuestionDetector
{
    public const int MaxCorrections = 2;

    /// <summary>How many of the offending questions we quote back before giving up on enumerating.</summary>
    public const int MaxQuotedQuestions = 5;

    public const string SingleQuestionInstruction =
        "Ask exactly ONE question per message. Never combine two questions " +
        "(no \"and what about…\" follow-ups in the same message). " +
        "Resolve the current question fully before asking the next.";

    // Without this, a question with a handful of discrete answers ("should X show a message, or
    // just stay blank?") gets phrased as ordinary prose with an inline "or" — the chat UI's
    // quick-reply buttons only recognize a bare trailing list, so a natural-language question
    // like that never gets buttons and the user has to type the answer out by hand every time.
    public const string QuickReplyFormattingInstruction =
        "When your question has 2 to 4 short, discrete answer choices (not open-ended), end the " +
        "message with those choices as a plain numbered list — one short option per line, e.g. " +
        "\"1. Option\" — with nothing after the list, no closing remark or summary sentence below " +
        "it. The chat UI turns a trailing list like that into clickable buttons for the user.";

    // Stable opening line so a correction prompt can be recognised by callers/tests without
    // string-matching the whole (now dynamic) body.
    public const string CorrectionPromptPrefix = "You asked several questions in one message:";

    private static readonly Regex EchoLineRegex = EchoLinePattern();
    private static readonly Regex QuestionSnippetRegex = QuestionSnippetPattern();
    private static readonly Regex LeadingListMarkerRegex = LeadingListMarkerPattern();

    [GeneratedRegex(@"^(?:tool:\s*[a-zA-Z_]+|call_[a-zA-Z0-9]{6,})\s*$", RegexOptions.Compiled)]
    private static partial Regex EchoLinePattern();

    // A run of characters with no sentence terminator, ending at a question mark. Long enough to
    // hold a real question, short enough not to swallow a whole paragraph of prose.
    [GeneratedRegex(@"(?<q>[^?!.\n]{4,300})\?", RegexOptions.Compiled)]
    private static partial Regex QuestionSnippetPattern();

    [GeneratedRegex(@"^\s*(?:\d+[.)]|[-*•])\s*", RegexOptions.Compiled)]
    private static partial Regex LeadingListMarkerPattern();

    public static bool ContainsMultipleQuestions(string? text) => CountQuestions(text) >= 2;

    public static int CountQuestions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var count = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (EchoLineRegex.IsMatch(line)) continue;
            foreach (var ch in line)
            {
                if (ch == '?') count++;
            }
        }
        return count;
    }

    /// <summary>
    /// The questions the agent actually asked, in order, cleaned of list markers and echoed
    /// tool-call lines. Used to quote them back — the correction must not make the model guess
    /// which of its messages we mean.
    /// </summary>
    public static IReadOnlyList<string> ExtractQuestions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var cleaned = string.Join(
            "\n",
            text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !EchoLineRegex.IsMatch(line)));

        var questions = new List<string>();
        foreach (Match match in QuestionSnippetRegex.Matches(cleaned))
        {
            var question = LeadingListMarkerRegex.Replace(match.Groups["q"].Value, string.Empty)
                .Trim()
                .Trim('"', '\'', '`', '-', ' ', ':', '(');
            if (question.Length < 3)
                continue;

            questions.Add(question.Length > 200 ? question[..200] + "…" : question);
            if (questions.Count >= MaxQuotedQuestions)
                break;
        }

        return questions;
    }

    /// <summary>
    /// A correction that quotes the offending questions verbatim and asks for exactly one of them.
    /// Grounding it matters because the model must not have to infer *which* message or *which*
    /// question we're objecting to; a generic "you asked multiple questions" invite can produce a
    /// reply that is itself a question ("which one?"), which just burns another correction.
    /// </summary>
    public static string BuildCorrectionPrompt(string? offendingText)
    {
        var questions = ExtractQuestions(offendingText);
        var builder = new StringBuilder();
        builder.AppendLine(CorrectionPromptPrefix);

        if (questions.Count >= 2)
        {
            for (var i = 0; i < questions.Count; i++)
                builder.Append("  ").Append(i + 1).Append(") ").AppendLine(questions[i]);
        }
        else if (!string.IsNullOrWhiteSpace(offendingText))
        {
            // Couldn't isolate the questions cleanly — quote the message itself so the model is
            // still looking at the real thing rather than from memory.
            var quoted = offendingText.Trim();
            if (quoted.Length > 600)
                quoted = quoted[..600] + "…";
            builder.AppendLine();
            builder.AppendLine(quoted);
        }

        builder.AppendLine();
        builder.AppendLine("Ask only ONE question now — the single most important thing you need to know.");
        builder.AppendLine("Re-send just that one question, in your own words, and nothing else.");
        builder.Append("Do not ask which one I want answered, and do not include any follow-up questions.");
        return builder.ToString();
    }

    /// <summary>True for a prompt produced by <see cref="BuildCorrectionPrompt"/>.</summary>
    public static bool IsCorrectionPrompt(string? text)
        => text is not null && text.StartsWith(CorrectionPromptPrefix, StringComparison.Ordinal);
}
