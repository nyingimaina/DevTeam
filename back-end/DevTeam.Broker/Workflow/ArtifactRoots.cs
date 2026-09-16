namespace DevTeam.Broker.Workflow;

// The fixed, closed set of named directory roots a WorkflowArtifact (or a placeholder in
// prompt text — see WorkflowEngine.ResolvePlaceholders) may resolve against. Mirrors
// BuiltinRegistry's shape.
public static class ArtifactRoots
{
    public const string DocsRoot = "docs-root";
    public const string FeatureDocsRoot = "feature-docs-root";
    public const string FeatureCodeRootBack = "feature-code-root-back";
    public const string FeatureCodeRootFront = "feature-code-root-front";
    public const string WorkspaceRoot = "workspace-root";

    public static IReadOnlySet<string> All => new HashSet<string>
    {
        DocsRoot,
        FeatureDocsRoot,
        FeatureCodeRootBack,
        FeatureCodeRootFront,
        WorkspaceRoot,
    };

    public static bool IsKnown(string name) => All.Contains(name);
}
