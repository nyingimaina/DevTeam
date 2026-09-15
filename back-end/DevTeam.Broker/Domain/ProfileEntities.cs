namespace DevTeam.Broker.Domain;

/// <summary>
/// A named, reusable bundle of per-stage priming prompts. Exactly one profile is ever
/// marked default once any exist — the first profile ever created becomes default
/// automatically; after that, changing it is explicit (see ApiEndpoints' set-default route).
/// </summary>
public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ProfilePrompt> Prompts { get; set; } = [];
}

/// <summary>
/// One profile's priming text for a single pipeline stage. StageName is whatever the
/// workspace's pipeline calls that stage — not restricted to a fixed set, since a workspace
/// can define its own custom devteam/release.yaml pipeline with different stage names.
/// </summary>
public sealed class ProfilePrompt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public Profile Profile { get; set; } = null!;
    public string StageName { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;

    /// <summary>
    /// When true, PromptText replaces the entire built-in prompt for this stage — including
    /// the handoff-automation notice, BRS-authoring instructions, and prior-stage context —
    /// instead of being appended after it. An explicit, user-accepted tradeoff: nothing else
    /// is sent to the agent, so a careless override can break handoff/gate detection.
    /// </summary>
    public bool OverridesBuiltInPrompt { get; set; }
}

/// <summary>
/// Remembers, per workspace, which profile is currently active there — mirrors
/// WorkspaceGitSettings. Absent until the workspace first resolves to the global default.
/// </summary>
public sealed class WorkspaceProfileSettings
{
    public string WorkspacePath { get; set; } = string.Empty;
    public Guid ProfileId { get; set; }
}
