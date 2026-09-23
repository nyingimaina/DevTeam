namespace DevTeam.Broker.Workflow;

/// <summary>
/// Plain-language labels for a stage's steps — what a person reads in the live checklist while a
/// stage runs. Steps are addressed by the same display identifiers
/// <c>WorkflowEngine.FlattenStepNames</c> produces, so the backend stays the single source of
/// truth and the UI never has to know what a builtin id means.
/// </summary>
public static class StepFriendlyText
{
    private const string AgentPrefix = "agent:";
    private const string GatePromptPrefix = "gate_prompt:";
    private const string RequiresSpecialistPrefix = "requires_specialist:";
    private const string RequiresArtifactPrefix = "requires_artifact:";

    public static string Describe(string stepName)
    {
        if (stepName.StartsWith(AgentPrefix, StringComparison.Ordinal))
            return $"{StageName(stepName[AgentPrefix.Length..])} agent working";
        if (stepName.StartsWith(GatePromptPrefix, StringComparison.Ordinal))
            return "A short review by the agent";
        if (stepName.StartsWith(RequiresSpecialistPrefix, StringComparison.Ordinal))
            return "Consulting a specialist";
        if (stepName.StartsWith(RequiresArtifactPrefix, StringComparison.Ordinal))
            return "Waiting for another stage's output";

        // Builtins already have plain-language titles (GateFriendlyText) — reuse them rather
        // than keeping a second copy of the same wording.
        return GateFriendlyText.Describe(stepName, null).Title;
    }

    /// <summary>Mirrors the frontend's stage labels so a mode name and a stage name never read differently.</summary>
    public static string StageName(string name) => name switch
    {
        "qa" => "QA",
        "business-analyst" => "Business Analyst",
        _ => Title(name),
    };

    private static string Title(string name)
        => string.Join(' ', name
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length <= 3 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]));
}
