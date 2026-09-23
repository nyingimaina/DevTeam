using System.Text.Json;

namespace DevTeam.Broker.Gates;

/// <summary>
/// The feature's code/core/shared scope as it stood the moment the developer stage first began —
/// frozen so slice_scope (and the QA-stage scope review) check the developer's committed work
/// against a boundary the developer itself cannot move. Without this, the developer has write
/// access to its own manifest.yaml (needed for legitimate free-form-hierarchy reasons — see
/// ScaffoldSpecsGate) and could otherwise self-expand codePaths/Shared, which a later retry's
/// fresh session would then pick straight back up, making slice_scope grade the developer's own
/// homework instead of enforcing a real boundary.
/// </summary>
public sealed record DeveloperScopeSnapshot(
    IReadOnlyList<string> CodePaths,
    string CoreBack,
    string CoreFront,
    IReadOnlyList<string> Shared);

public static class DeveloperScopeSnapshotIO
{
    // Deliberately NOT under devteam/features/<F> — that directory is inside the developer's own
    // write scope (ResolveAllowedWritePrefixes), so a snapshot stored there could be self-edited
    // exactly like manifest.yaml is today.
    public static string Path(string workspacePath, string featureKey)
        => System.IO.Path.Combine(workspacePath, "devteam", "scope-snapshots", featureKey + ".json");

    public static bool Exists(string workspacePath, string featureKey) => File.Exists(Path(workspacePath, featureKey));

    public static void WriteIfAbsent(string workspacePath, string featureKey, DeveloperScopeSnapshot snapshot)
    {
        var path = Path(workspacePath, featureKey);
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
    }

    public static DeveloperScopeSnapshot? TryRead(string workspacePath, string featureKey)
    {
        var path = Path(workspacePath, featureKey);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DeveloperScopeSnapshot>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
