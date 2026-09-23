using DevTeam.Broker.Gates;
using DevTeam.Broker.Gates.Readiness;

namespace DevTeam.Tests.Gates.Readiness;

public class ReadinessMetricsParserTests
{
    [Fact]
    public void ParseTestOutput_ReadsDotnetCounts()
    {
        var metrics = ReadinessMetricsParser.ParseTestOutput(
            "Build succeeded.\nPassed!  - Failed: 0, Passed: 42, Skipped: 0, Total: 42, Duration: 8 s");

        Assert.Equal(42, metrics.TestsPassed);
        Assert.Equal(0, metrics.TestsFailed);
    }

    [Fact]
    public void ParseTestOutput_ReadsJestCounts()
    {
        var metrics = ReadinessMetricsParser.ParseTestOutput("Tests: 7 failed, 38 passed, 45 total");

        Assert.Equal(38, metrics.TestsPassed);
        Assert.Equal(7, metrics.TestsFailed);
    }

    [Fact]
    public void ParseTestOutput_IsEmptyWhenNothingTestLikeWasPrinted()
    {
        var metrics = ReadinessMetricsParser.ParseTestOutput("Compiled 3 files successfully.");

        Assert.Equal(ReadinessMetrics.Empty, metrics);
    }

    [Fact]
    public void ParseJestCoverageJson_ReadsTotals()
    {
        const string json = """
        {
          "total": {
            "lines": { "total": 100, "covered": 88, "pct": 88 },
            "branches": { "pct": 75.5 },
            "functions": { "pct": 90 },
            "statements": { "pct": 88 }
          }
        }
        """;

        var metrics = ReadinessMetricsParser.ParseJestCoverageJson(json);

        Assert.Equal(88, metrics.LineCoverage);
        Assert.Equal(75.5, metrics.BranchCoverage);
        Assert.Equal(90, metrics.FunctionCoverage);
    }

    [Fact]
    public void ParseJestCoverageJson_IsEmptyForMalformedJson()
    {
        Assert.Equal(ReadinessMetrics.Empty, ReadinessMetricsParser.ParseJestCoverageJson("{not json"));
    }

    [Fact]
    public void ParseCobertura_ConvertsRatesToPercentages()
    {
        var metrics = ReadinessMetricsParser.ParseCobertura(
            """<coverage line-rate="0.85" branch-rate="0.7" version="1.9"></coverage>""");

        Assert.Equal(85, metrics.LineCoverage);
        Assert.Equal(70, metrics.BranchCoverage);
    }

    [Fact]
    public void ThresholdProblem_FlagsCoverageBelowTheFloor()
    {
        var phase = PhaseWithCoverage(new ReadinessCoverageSpec("c.json", LineMin: 80));

        var problem = ReadinessMetricsParser.ThresholdProblem(phase, new ReadinessMetrics(LineCoverage: 70));

        Assert.NotNull(problem);
        Assert.Contains("below the required 80%", problem);
    }

    [Fact]
    public void ThresholdProblem_FlagsUnmeasurableCoverageWhenAFloorIsSet()
    {
        var phase = PhaseWithCoverage(new ReadinessCoverageSpec("c.json", LineMin: 80));

        var problem = ReadinessMetricsParser.ThresholdProblem(phase, ReadinessMetrics.Empty);

        Assert.NotNull(problem);
        Assert.Contains("couldn't be measured", problem);
    }

    [Fact]
    public void ThresholdProblem_IsNullWhenTheFloorIsMet()
    {
        var phase = PhaseWithCoverage(new ReadinessCoverageSpec("c.json", LineMin: 80));

        Assert.Null(ReadinessMetricsParser.ThresholdProblem(phase, new ReadinessMetrics(LineCoverage: 90)));
    }

    [Fact]
    public void ThresholdProblem_IsNullWithoutACoverageSpec()
        => Assert.Null(ReadinessMetricsParser.ThresholdProblem(
            new ReadinessPhaseDefinition("x", "X", "echo hi"), new ReadinessMetrics(LineCoverage: 1)));

    private static ReadinessPhaseDefinition PhaseWithCoverage(ReadinessCoverageSpec spec)
        => new("frontend-tests", "Tests", "npx jest", Coverage: spec);
}
