using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class GherkinAndCoverageGateTests
{
    private const string RequirementsJson =
        """[{"id":"REQ-001","title":"User can log in","acceptanceCriteria":"Given a user, When the form is valid, Then they are signed in"},{"id":"REQ-002","title":"User can log out","acceptanceCriteria":"Only a heading"}]""";

    private static GateRequest Request(string builtin, IReadOnlyDictionary<string, string>? inputs = null)
        => new(builtin, @"C:\work\proj", "feat-001", "developer", inputs);

    [Fact]
    public async Task Gherkin_RejectsRequirementMissingKeywords()
    {
        var gate = new GherkinValidatorGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.GherkinValidator, new Dictionary<string, string> { ["requirementsJson"] = RequirementsJson }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("REQ-002", result.EvidenceText);
        Assert.Contains("ok:   REQ-001", result.EvidenceText);
    }

    [Fact]
    public async Task Gherkin_PassesWhenAllRequirementsAreComplete()
    {
        var gate = new GherkinValidatorGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.GherkinValidator, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson.Replace(
                    "Only a heading",
                    "Given a user, When they choose log out, Then the session ends"),
            }),
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("All 2 requirements", result.Reason);
    }

    [Fact]
    public async Task Coverage_FailsOnUnreferencedRequirements()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testOutput"] = "tests: 5 passed, 0 failed\nREQ-001 ok",
            }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("REQ-002", result.EvidenceText);
        Assert.Contains("ok:   covered REQ-001", result.EvidenceText);
    }

    [Fact]
    public async Task Coverage_PassesWhenEveryRequirementIsReferenced()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testOutput"] = "REQ-001 and REQ-002 both appear in test names",
            }),
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("| REQ-001 | covered |", result.EvidenceText);
    }

    [Fact]
    public async Task Coverage_PassesWhenTestSourceFilesReferenceRequirement()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testOutput"] = "No test output captured.",
                ["testFilesJson"] = """[{"path":"CalculatorLib.Tests/AuthTests.cs","content":"[Theory] REQ-001 login REQ-002 logout scenarios"}]""",
            }),
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("| REQ-002 | covered |", result.EvidenceText);
    }

    [Fact]
    public async Task Coverage_PassesWhenCompactIdVariantAppearsInTests()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testFilesJson"] = """[{"path":"CalculatorLib.Tests/AuthTests.cs","content":"REQ001_Login_Succeeds and REQ002_Logout_Succeeds"}]""",
            }),
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("| REQ-001 | covered |", result.EvidenceText);
        Assert.Contains("| REQ-002 | covered |", result.EvidenceText);
    }

    [Fact]
    public async Task Coverage_FailsWhenTestSourceFilesDoNotReferenceRequirements()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testFilesJson"] = """[{"path":"CalculatorLib.Tests/AuthTests.cs","content":"no requirement ids anywhere"}]""",
            }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("REQ-001", result.EvidenceText);
        Assert.Contains("REQ-002", result.EvidenceText);
    }

    // The standardized convention is "name the requirement in the test". These cases pin the
    // spellings a developer is likely to use so the check accepts any of them.

    [Fact]
    public async Task Coverage_PassesWhenIdUsesUnderscoreSeparator()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testFilesJson"] = """[{"path":"CalculatorLib.Tests/AuthTests.cs","content":"REQ_001_Login_Succeeds and REQ_002_Logout_Succeeds"}]""",
            }),
            CancellationToken.None);

        Assert.True(result.Passed, result.EvidenceText);
    }

    [Fact]
    public async Task Coverage_PassesWhenIdDropsZeroPadding()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testFilesJson"] = """[{"path":"CalculatorLib.Tests/AuthTests.cs","content":"REQ_1_Login_Succeeds and REQ_2_Logout_Succeeds"}]""",
            }),
            CancellationToken.None);

        Assert.True(result.Passed, result.EvidenceText);
    }

    [Fact]
    public async Task Coverage_DoesNotCoverRequirementOneFromRequirementTen()
    {
        var gate = new CoverageMatrixGate();
        var requirements = """[{"id":"REQ-1","title":"One","acceptanceCriteria":"Given a, When b, Then c"},{"id":"REQ-10","title":"Ten","acceptanceCriteria":"Given a, When b, Then c"}]""";

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = requirements,
                ["testFilesJson"] = """[{"path":"T.cs","content":"REQ_10_Ten_Works"}]""",
            }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Matches(@"fail: no test references REQ-1\b", result.EvidenceText);
        Assert.Contains("ok:   covered REQ-10", result.EvidenceText);
    }

    [Fact]
    public async Task Coverage_FailureTellsTheDeveloperHowToNameTests()
    {
        var gate = new CoverageMatrixGate();

        var result = await gate.RunAsync(
            Request(BuiltinRegistry.CoverageMatrix, new Dictionary<string, string>
            {
                ["requirementsJson"] = RequirementsJson,
                ["testOutput"] = "5 passed",
            }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("requirement id", result.EvidenceText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REQ-001", result.EvidenceText);
    }
}