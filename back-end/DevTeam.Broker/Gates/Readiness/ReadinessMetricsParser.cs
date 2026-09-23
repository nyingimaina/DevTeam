using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DevTeam.Broker.Gates.Readiness;

/// <summary>
/// Extracts the numbers a report wants to chart (test counts, coverage) out of raw command
/// output and coverage files. Deliberately tolerant: a phase that produces nothing parseable
/// yields <see cref="ReadinessMetrics.Empty"/> rather than throwing — metrics are for display,
/// never the pass/fail decision itself (except an explicit coverage minimum, see
/// <see cref="ThresholdProblem"/>).
/// </summary>
public static partial class ReadinessMetricsParser
{
    public static ReadinessMetrics ParseTestOutput(string rawOutput)
    {
        var normalized = TestOutputNormalizer.Normalize(rawOutput);
        var match = TestSummary().Match(normalized);
        if (!match.Success)
            return ReadinessMetrics.Empty;

        return new ReadinessMetrics(
            TestsPassed: int.Parse(match.Groups["passed"].Value),
            TestsFailed: int.Parse(match.Groups["failed"].Value));
    }

    /// <summary>Jest's coverage-summary.json ({"total": {"lines": {"pct": 88.5}, ...}}).</summary>
    public static ReadinessMetrics ParseJestCoverageJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("total", out var total))
                return ReadinessMetrics.Empty;

            return new ReadinessMetrics(
                LineCoverage: Percent(total, "lines"),
                BranchCoverage: Percent(total, "branches"),
                FunctionCoverage: Percent(total, "functions"));
        }
        catch (JsonException)
        {
            return ReadinessMetrics.Empty;
        }
    }

    /// <summary>Cobertura XML, whose rates are 0–1 fractions on the root element.</summary>
    public static ReadinessMetrics ParseCobertura(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null || !root.Name.LocalName.Equals("coverage", StringComparison.Ordinal))
                return ReadinessMetrics.Empty;

            return new ReadinessMetrics(
                LineCoverage: Fraction(root.Attribute("line-rate")),
                BranchCoverage: Fraction(root.Attribute("branch-rate")));
        }
        catch (System.Xml.XmlException)
        {
            return ReadinessMetrics.Empty;
        }
    }

    /// <summary>
    /// A plain-language reason the phase failed its coverage floor, or null when it met it (or
    /// no floor is configured). A configured floor with no measured number is itself a failure —
    /// "we asked for 80% and couldn't tell" must not silently pass.
    /// </summary>
    public static string? ThresholdProblem(ReadinessPhaseDefinition phase, ReadinessMetrics metrics)
    {
        var spec = phase.Coverage;
        if (spec is null)
            return null;

        return Check(spec.LineMin, metrics.LineCoverage, "Line")
            ?? Check(spec.BranchMin, metrics.BranchCoverage, "Branch")
            ?? Check(spec.FunctionMin, metrics.FunctionCoverage, "Function");
    }

    private static string? Check(double? min, double? actual, string label)
    {
        if (min is null)
            return null;

        if (actual is not { } value)
            return $"{label} coverage couldn't be measured, but at least {min:0.#}% is required.";

        return value < min
            ? $"{label} coverage is {value:0.#}%, below the required {min:0.#}%."
            : null;
    }

    private static double? Percent(JsonElement total, string kind)
    {
        if (!total.TryGetProperty(kind, out var element) ||
            !element.TryGetProperty("pct", out var pct) ||
            pct.ValueKind != JsonValueKind.Number ||
            !pct.TryGetDouble(out var value))
            return null;

        return Math.Round(value, 1);
    }

    private static double? Fraction(XAttribute? attribute)
        => attribute is not null && double.TryParse(attribute.Value, out var rate)
            ? Math.Round(rate * 100, 1)
            : null;

    [GeneratedRegex(@"tests:\s+(?<passed>\d+)\s+passed,\s+(?<failed>\d+)\s+failed")]
    private static partial Regex TestSummary();
}
