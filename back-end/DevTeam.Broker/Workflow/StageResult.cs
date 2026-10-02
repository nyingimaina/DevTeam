using System.Text;
using System.Text.Json;

using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;

namespace DevTeam.Broker.Workflow;

public enum StageVerdict
{
    /// <summary>The stage did what it was asked.</summary>
    Done,

    /// <summary>The stage cannot satisfy something as scoped - a person decides.</summary>
    Blocked,

    /// <summary>The stage believes a check or requirement is wrong - a person decides.</summary>
    Disputed,
}

/// <summary>One numbered point the stage was handed, and how it answered it.</summary>
public sealed record StagePointAnswer(int Number, ReviewFindingResponse Kind, string Detail);

/// <summary>
/// What a stage hands to whoever acts next: a verdict from a closed set, a summary in its own
/// words, and an answer to every point it was given. Until now the only thing crossing a stage
/// boundary was a regenerated context.md (no summary, no decisions) plus a free-text "DONE", so the
/// next stage - and the negotiation machinery - had to guess what the previous one meant.
/// </summary>
public sealed record StageResult(
    StageVerdict Verdict, string Summary, IReadOnlyList<StagePointAnswer> Points, bool Synthesized = false);

public sealed record StageResultRead(StageResult? Result, string? Problem)
{
    public bool Ok => Result is not null;
}

public static class StageResultIO
{
    private const int MaxSummaryChars = 1500;

    public static string FileName(string role) => $"stage-result.{role}.json";

    public static string PathFor(string workspacePath, string featureKey, string role)
        => Path.Combine(ArtifactPaths.FeatureDir(workspacePath, featureKey), FileName(role));

    /// <summary>The instruction appended to a stage's prompt: the contract for what it writes last.</summary>
    public static string Instruction(string featureKey, string role) =>
        $"\n\nWhen you are done, write {ArtifactPaths.FeatureDirRelative(featureKey).Replace('\\', '/')}/{FileName(role)} " +
        "containing exactly this JSON and nothing else: " +
        "{ \"verdict\": \"done\" | \"blocked\" | \"disputed\", \"summary\": \"<what you changed and what the next stage must know>\", " +
        "\"points\": [ { \"n\": <point number>, \"kind\": \"ADDRESSED\" | \"DISPUTED\" | \"BLOCKED\", \"detail\": \"<files / why>\" } ] }. " +
        "Use \"blocked\" or \"disputed\" only when you genuinely cannot proceed or believe a check is wrong - " +
        "a person will then decide; do not retry or work around it. \"points\" is your answer to every numbered point " +
        "you were given above (answer them here, not in prose); it may be empty when you were given none.";

    /// <summary>
    /// Maps the file's point answers onto the open points by number (1-based, the order they were
    /// listed to the stage). A point the file never mentions stays unanswered.
    /// </summary>
    public static IReadOnlyList<PointResponse> AnswerPoints(StageResult result, IReadOnlyList<ReviewFinding> openPoints)
    {
        var answers = new List<PointResponse>(openPoints.Count);
        for (var i = 0; i < openPoints.Count; i++)
        {
            var answer = result.Points.LastOrDefault(p => p.Number == i + 1);
            answers.Add(answer is null
                ? new PointResponse(openPoints[i], ReviewFindingResponse.None, string.Empty)
                : new PointResponse(openPoints[i], answer.Kind, answer.Detail));
        }

        return answers;
    }

    public static StageResultRead TryRead(string workspacePath, string featureKey, string role)
    {
        var path = PathFor(workspacePath, featureKey, role);
        if (!File.Exists(path))
            return new StageResultRead(null, $"{FileName(role)} was not written.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new StageResultRead(null, $"{FileName(role)} must be a JSON object.");

            if (!root.TryGetProperty("verdict", out var verdictElement)
                || !TryParseVerdict(verdictElement.GetString(), out var verdict))
                return new StageResultRead(null, "\"verdict\" must be one of: done, blocked, disputed.");

            var summary = root.TryGetProperty("summary", out var summaryElement) ? summaryElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(summary))
                return new StageResultRead(null, "\"summary\" is required: say what you changed and what the next stage must know.");

            var points = new List<StagePointAnswer>();
            if (root.TryGetProperty("points", out var pointsElement) && pointsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in pointsElement.EnumerateArray())
                {
                    var kindText = point.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    if (!TryParseKind(kindText, out var kind))
                        return new StageResultRead(null, $"A point's \"kind\" must be ADDRESSED, DISPUTED or BLOCKED (got \"{kindText}\").");
                    var number = point.TryGetProperty("n", out var n) && n.TryGetInt32(out var value) ? value : 0;
                    var detail = point.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                    points.Add(new StagePointAnswer(number, kind, detail));
                }
            }

            var synthesized = root.TryGetProperty("synthesized", out var s) && s.ValueKind == JsonValueKind.True;
            return new StageResultRead(new StageResult(verdict, summary.Trim(), points, synthesized), null);
        }
        catch (JsonException ex)
        {
            return new StageResultRead(null, $"{FileName(role)} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// What stands in when the agent never wrote the file even after a nudge: its last reply as the
    /// summary, flagged so nobody reads it as a considered verdict. The pipeline keeps moving; the
    /// next stage still gets something better than a template.
    /// </summary>
    public static StageResult Synthesize(string? lastReply)
    {
        var text = string.IsNullOrWhiteSpace(lastReply) ? "(the agent left no summary)" : lastReply.Trim();
        if (text.Length > MaxSummaryChars) text = text[..MaxSummaryChars] + "…";
        return new StageResult(StageVerdict.Done, text, [], Synthesized: true);
    }

    public static void Write(string workspacePath, string featureKey, string role, StageResult result)
    {
        var path = PathFor(workspacePath, featureKey, role);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            verdict = result.Verdict.ToString().ToLowerInvariant(),
            summary = result.Summary,
            synthesized = result.Synthesized,
            points = result.Points.Select(p => new { n = p.Number, kind = p.Kind.ToString().ToUpperInvariant(), detail = p.Detail }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The previous stage's result, shaped as a prompt section for the stage that follows it.</summary>
    public static string BuildContext(string workspacePath, string featureKey, string? previousRole)
    {
        if (string.IsNullOrWhiteSpace(previousRole)) return string.Empty;
        var read = TryRead(workspacePath, featureKey, previousRole);
        if (!read.Ok) return string.Empty;

        var result = read.Result!;
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine($"--- Result of the previous stage ({previousRole}) ---");
        builder.AppendLine($"Verdict: {result.Verdict.ToString().ToLowerInvariant()}{(result.Synthesized ? " (inferred - the agent wrote no result file)" : string.Empty)}");
        builder.Append(result.Summary);
        return builder.ToString();
    }

    private static bool TryParseVerdict(string? text, out StageVerdict verdict)
    {
        verdict = default;
        return text is not null && Enum.TryParse(text.Trim(), ignoreCase: true, out verdict)
            && Enum.IsDefined(verdict);
    }

    private static bool TryParseKind(string? text, out ReviewFindingResponse kind)
    {
        kind = ReviewFindingResponse.None;
        if (text is null || !Enum.TryParse(text.Trim(), ignoreCase: true, out kind)) return false;
        return kind is ReviewFindingResponse.Addressed or ReviewFindingResponse.Disputed or ReviewFindingResponse.Blocked;
    }
}
