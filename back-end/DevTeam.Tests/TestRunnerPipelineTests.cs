using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class TestRunnerPipelineTests
{
    private static WorkflowDefinition Default() => new WorkflowDefinitionLoader().LoadDefault();

    private static WorkflowRole Role(string name)
        => Default().Pipeline.FirstOrDefault(r => r.Name == name)
            ?? throw new InvalidOperationException("no such role: " + name);

    [Fact]
    public void TestRunnerStage_SitsBetweenDeveloperAndQa()
    {
        var order = Default().Pipeline.Select(r => r.Name).ToList();

        var testRunner = order.IndexOf("test-runner");
        Assert.True(testRunner > 0, "the test-runner stage must exist");
        Assert.True(order.IndexOf("developer") < testRunner, "test-runner must come after developer");
        Assert.True(testRunner < order.IndexOf("qa"), "test-runner must come before qa");
    }

    [Fact]
    public void Developer_NoLongerGatesOnTheTestSuite_ItGetsTheFastLane()
    {
        var loop = Assert.Single(DeveloperSteps(), s => s.Kind == WorkflowStepKind.Loop);
        var builtins = Flatten(loop.LoopSteps!).Select(s => s.Builtin).ToArray();

        Assert.Contains(BuiltinRegistry.FastLane, builtins);
        Assert.DoesNotContain(BuiltinRegistry.VerifyCode, builtins);
    }

    [Fact]
    public void TestRunner_RunsTheSuiteAuthorsAReportAndJudgesItAgainstTheDeveloper()
    {
        var role = Role("test-runner");
        var builtins = role.Steps.Where(s => s.Kind == WorkflowStepKind.Builtin).Select(s => s.Builtin).ToList();

        Assert.Equal(
            [BuiltinRegistry.ContextBundle, BuiltinRegistry.TestRun, BuiltinRegistry.TestReport],
            builtins);

        var agent = Assert.Single(role.Steps, s => s.Kind == WorkflowStepKind.Agent);
        Assert.Equal("test-runner", agent.AgentMode);

        var report = role.Steps.Single(s => s.Builtin == BuiltinRegistry.TestReport);
        Assert.Equal("developer", report.ResponsibleRole);
    }

    [Fact]
    public void TestRunner_RunsAutonomously_AndWritesNoCode()
    {
        var role = Role("test-runner");

        Assert.False(role.UserInputRequired, "a green suite must never wait on a human");
        Assert.False(role.WritesCode, "the test runner judges; it does not fix");
        Assert.Null(role.Signoff);
        Assert.Contains(role.ExpectedArtifacts, a => a.EndsWith(ArtifactPaths.TestReportFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void TheOperatorRulingPathIsAMessage_NotAStageThatBlocksOnInput()
    {
        // The operator rules by messaging whatever stage is live - SendMessageAsync targets the
        // current stage regardless of UserInputRequired - so a challenge never needs the stage to
        // have demanded input up front.
        var engine = typeof(WorkflowEngine).GetMethod("SendMessageAsync");
        Assert.NotNull(engine);

        var testRunner = Role("test-runner");
        Assert.Contains("operator rules by messaging this stage", testRunner.SeedPrompt, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Qa_KeepsItsOwnIndependentTestRun()
    {
        Assert.Contains(BuiltinRegistry.VerifyCode, Role("qa").Steps.Select(s => s.Builtin));
    }

    [Fact]
    public void TheNewGatesAreRegisteredBuiltins()
    {
        Assert.Contains(BuiltinRegistry.TestRun, BuiltinRegistry.All);
        Assert.Contains(BuiltinRegistry.TestReport, BuiltinRegistry.All);
        Assert.Contains(BuiltinRegistry.FastLane, BuiltinRegistry.All);
    }

    private static IReadOnlyList<WorkflowStep> DeveloperSteps() => Role("developer").Steps;

    private static IEnumerable<WorkflowStep> Flatten(IReadOnlyList<WorkflowStep> steps)
        => steps.SelectMany(step => step.LoopSteps is null ? [step] : Flatten(step.LoopSteps));
}
