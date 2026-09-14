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

    public const string SingleQuestionInstruction =
        "Ask exactly ONE question per message. Never combine two questions " +
        "(no \"and what about…\" follow-ups in the same message). " +
        "Resolve the current question fully before asking the next.";

    public const string CorrectionPrompt =
        "You asked multiple questions at once. Re-send ONLY your single most " +
        "important question to the user right now. Do not ask a second question " +
        "and do not list follow-ups in this message.";

    private static readonly Regex EchoLineRegex = EchoLinePattern();

    [GeneratedRegex(@"^(?:tool:\s*[a-zA-Z_]+|call_[a-zA-Z0-9]{6,})\s*$", RegexOptions.Compiled)]
    private static partial Regex EchoLinePattern();

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
}