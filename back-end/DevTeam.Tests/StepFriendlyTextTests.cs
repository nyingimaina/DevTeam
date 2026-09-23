using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class StepFriendlyTextTests
{
    [Theory]
    [InlineData("context_bundle", "Gathering background for the agent")]
    [InlineData("verify_code", "The code builds and its tests pass")]
    [InlineData("final_checks", "Every final check passes")]
    public void Describe_ReusesThePlainLanguageGateWordingForBuiltins(string step, string expected)
        => Assert.Equal(expected, StepFriendlyText.Describe(step));

    [Theory]
    [InlineData("agent:qa", "QA agent working")]
    [InlineData("agent:developer", "Developer agent working")]
    [InlineData("agent:business-analyst", "Business Analyst agent working")]
    public void Describe_ReadsAgentStepsAsEnglish(string step, string expected)
        => Assert.Equal(expected, StepFriendlyText.Describe(step));

    [Fact]
    public void Describe_NamesTheOtherStepKindsPlainly()
    {
        Assert.Equal("A short review by the agent", StepFriendlyText.Describe("gate_prompt:does it work?"));
        Assert.Equal("Consulting a specialist", StepFriendlyText.Describe("requires_specialist:database-admin"));
        Assert.Equal("Waiting for another stage's output", StepFriendlyText.Describe("requires_artifact:business-analyst"));
    }

    [Fact]
    public void Describe_HumanizesAnUnknownBuiltin_InsteadOfShowingAnId()
    {
        var label = StepFriendlyText.Describe("some_future_builtin");

        Assert.DoesNotContain("_", label);
        Assert.Equal("Some future builtin", label);
    }

    [Fact]
    public void Describe_NeverLeaksAnIdentifierForAnyKnownStep()
    {
        // The identifiers FlattenStepNames produces for the default pipeline.
        string[] identifiers =
        [
            "scaffold_specs", "context_bundle", "gherkin_validator", "verify_code", "code_hygiene",
            "slice_scope", "reuse_gate", "render_pr", "render_handoff", "coverage_matrix", "final_checks",
            "agent:business-analyst", "agent:developer", "agent:qa",
        ];

        foreach (var identifier in identifiers)
        {
            var label = StepFriendlyText.Describe(identifier);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotContain("_", label);
            Assert.DoesNotContain(":", label);
        }
    }
}
