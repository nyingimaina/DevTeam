namespace DevTeam.Broker.Gates;

public static class ArtifactPaths
{
    public static string FeatureDirRelative(string featureKey)
        => Path.Combine("devteam", "features", featureKey);

    public static string FeatureDir(string workspacePath, string featureKey)
        => Path.Combine(workspacePath, FeatureDirRelative(featureKey));

    public static string ManifestPath(string workspacePath, string featureKey)
        => Path.Combine(FeatureDir(workspacePath, featureKey), "manifest.yaml");

    // The Business Requirements Specification — the business-analyst's authored contract
    // document. A single named constant so every gate/prompt that reads or writes it agrees
    // on the filename.
    public const string BrsFileName = "BRS.md";

    public static string BrsPath(string workspacePath, string featureKey)
        => Path.Combine(FeatureDir(workspacePath, featureKey), BrsFileName);

    public static string ContextPath(string workspacePath, string featureKey)
        => Path.Combine(FeatureDir(workspacePath, featureKey), "context.md");

    public static string HandoffPath(string workspacePath, string featureKey)
        => Path.Combine(FeatureDir(workspacePath, featureKey), "handoff.md");

    public static string ReleaseYamlPath(string workspacePath)
        => Path.Combine(workspacePath, "devteam", "release.yaml");
}

public static class RequirementDtos
{
    public sealed record Requirement(string Id, string Title, string AcceptanceCriteria);

    public static IReadOnlyList<Requirement> ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Requirement>>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return parsed ?? [];
    }
}