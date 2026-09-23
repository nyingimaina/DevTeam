namespace DevTeam.Broker.Domain;

/// <summary>
/// Remembers, per workspace, which AI model the person last chose — so every later stage (and
/// every later release in that project) starts on it instead of the built-in default. Mirrors
/// WorkspaceProfileSettings. Absent until a model is first chosen for the workspace.
/// </summary>
public sealed class WorkspaceModelSettings
{
    public string WorkspacePath { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
