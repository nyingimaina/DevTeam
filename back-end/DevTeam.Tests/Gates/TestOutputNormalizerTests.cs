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
    public void DotnetFailedTest_IsReportedByName()
    {
        var raw = new[]
        {
            "  Failed DevTeam.Tests.WorkflowDefinitionLoaderTests.Load_AgentWithoutMode_Throws [11 ms]",
            "  Error Message:",
            "     Assert.Throws() Failure",
            "     at DevTeam.Tests.WorkflowDefinitionLoaderTests.Load_AgentWithoutMode_Throws() in /src/WorkflowTests.cs:line 88",
        };

        var normalized = TestOutputNormalizer.Normalize(string.Join('\n', raw));

        Assert.Contains("fail: DevTeam.Tests.WorkflowDefinitionLoaderTests.Load_AgentWithoutMode_Throws", normalized);
    }

    [Fact]
    public void StackFrames_AreNeverFailures()
    {
        var raw = new[]
        {
            "  Failed DevTeam.Tests.WorkflowDefinitionLoaderTests.Load_AgentWithoutMode_Throws [11 ms]",
            "     at Program.Main() in /src/Program.cs:line 12",
        };

        var normalized = TestOutputNormalizer.Normalize(string.Join('\n', raw));

        Assert.DoesNotContain("fail: /src/Program.cs:12", normalized);
        Assert.DoesNotContain("fail: Program.cs", normalized);
    }

    [Fact]
    public void VendorStackFrames_AreNotFailures()
    {
        var raw = string.Join('\n',
        [
            "  console.error",
            "    Warning: An update to ZestButton inside a test was not wrapped in act(...)",
            "    at node_modules/react-dom/cjs/react-dom-test-utils.development.js:129:18",
            "    at node_modules/react-dom/cjs/react-dom.development.js:45851:13",
            "    at ZestResponsiveLayout.test.tsx:41:12",
            "console.error",
            "    Warning: Each child in a list should have a unique \"key\" prop.",
            "    at node_modules/react-dom/cjs/react-dom.development.js:9215:7",
        ]);

        var normalized = TestOutputNormalizer.Normalize(raw);

        Assert.DoesNotContain("fail:", normalized);
    }

    [Fact]
    public void ConsoleWarnings_AreNotFailures()
    {
        var raw = string.Join('\n',
        [
            "warning CS8618: Non-nullable property must contain a non-null value when exiting",
            "  C:\\src\\Widget.cs(12,18): warning CS8618: Non-nullable property",
            "console.warn",
            "  Warning: deprecated api",
        ]);

        var normalized = TestOutputNormalizer.Normalize(raw);

        Assert.DoesNotContain("fail:", normalized);
    }

    [Fact]
    public void GreenSummary_WithFilelineNoise_ProducesNoFailures()
    {
        var raw = string.Join('\n',
        [
            "Tests:       0 failed, 120 passed, 120 total",
            "  at node_modules/react-dom/cjs/react-dom.development.js:45851:13",
            "  at ZestResponsiveLayout.test.tsx:41:12",
        ]);

        var normalized = TestOutputNormalizer.Normalize(raw);

        Assert.Contains("tests: 120 passed, 0 failed", normalized);
        Assert.DoesNotContain("fail:", normalized);
    }

    [Fact]
    public void JestFailureBlocks_AreReportedByTestName()
    {
        var raw = string.Join('\n',
        [
            "FAIL app/ReleaseWizard.test.tsx",
            "  \u25CF Release wizard \u203A saves settings",
            "",
            "    expect(received).toEqual(expected)",
            "",
            "Tests:       1 failed, 12 passed, 13 total",
        ]);

        var normalized = TestOutputNormalizer.Normalize(raw);

        Assert.Contains("fail: Release wizard \u203A saves settings", normalized);
    }

    [Fact]
    public void JestSummary_FailedCount_ComesFromSummary()
    {
        var raw = "Test Suites: 1 failed, 8 passed, 9 total\nTests:       7 failed, 38 passed, 45 total";

        var normalized = TestOutputNormalizer.Normalize(raw);

        Assert.Equal(7, TestOutputNormalizer.FailedCount(normalized));
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
