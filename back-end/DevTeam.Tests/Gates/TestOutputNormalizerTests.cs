using DevTeam.Broker.Gates;

namespace DevTeam.Tests;

public class TestOutputNormalizerTests
{
    [Fact]
    public void DotnetSummary_IsCondensed()
    {
        var raw = new[]
        {
            "Starting test execution, please wait...",
            "A total of 1 test files matched the specified pattern.",
            "Passed!  - Failed: 1, Passed: 41, Skipped: 3, Total: 45, Duration: 8 s - DevTeam.Tests.dll (net10.0)",
        };

        var normalized = TestOutputNormalizer.Normalize(string.Join('\n', raw));

        Assert.Contains("tests: 41 passed, 1 failed", normalized);
        Assert.DoesNotContain("Starting test execution", normalized);
    }

    [Fact]
    public void JestSummary_IsCondensed()
    {
        var raw = "Test Suites: 1 failed, 8 passed, 9 total\nTests:       7 failed, 38 passed, 45 total";

        var normalized = TestOutputNormalizer.Normalize(raw);

        Assert.Contains("tests: 38 passed, 7 failed", normalized);
    }

    [Fact]
    public void FailureLocations_AreKeptAsFileLine()
    {
        var raw = new[]
        {
            "  Failed DevTeam.Tests.WorkflowDefinitionLoaderTests.Load_AgentWithoutMode_Throws [11 ms]",
            "  Message:",
            "     at DevTeam.Tests.WorkflowDefinitionLoaderTests.Load_AgentWithoutMode_Throws() in /src/WorkflowTests.cs:line 88",
            "     at Program.Main() in /src/Program.cs:line 12",
        };

        var normalized = TestOutputNormalizer.Normalize(string.Join('\n', raw));

        Assert.Contains("/src/WorkflowTests.cs:88", normalized);
        Assert.Contains("/src/Program.cs:12", normalized);
    }

    [Fact]
    public void MissingPattern_ReturnsFirstLinesTruncated()
    {
        var normalized = TestOutputNormalizer.Normalize("hello\nworld\n");
        Assert.Equal("hello\nworld", normalized);
    }

    [Fact]
    public void EmptyOutput_ReportsNoOutput()
    {
        Assert.Equal("No test output captured.", TestOutputNormalizer.Normalize("   "));
    }
}