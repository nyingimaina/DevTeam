namespace DevTeam.Broker.Domain;

/// <summary>
/// Remembers, per workspace, which named PAT (see IGitCredentialStore) to use
/// when pushing or deleting a remote branch there. The credential itself is
/// shareable across many workspaces; this is just the workspace's current pick.
/// </summary>
public sealed class WorkspaceGitSettings
{
    public string WorkspacePath { get; set; } = string.Empty;
    public string? CredentialName { get; set; }
}
