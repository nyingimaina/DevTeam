namespace DevTeam.Broker.Workflow;

/// <summary>
/// The character split of a prompt, so a bloated prompt can be traced to the section that caused
/// it (e.g. the artifact context re-injecting the whole BRS every stage). Lengths only — never the
/// text — so it is cheap to compute and safe to persist.
/// </summary>
public sealed record PromptComposition(
    int Base,
    int Seed,
    int Profile,
    int Handoff,
    int Delegation,
    int Guidance,
    int Points,
    int Artifact,
    int Interview)
{
    public int Total => Base + Seed + Profile + Handoff + Delegation + Guidance + Points + Artifact + Interview;

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}
