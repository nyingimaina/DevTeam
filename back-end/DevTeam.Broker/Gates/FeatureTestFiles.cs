namespace DevTeam.Broker.Gates;

/// <summary>
/// Discovers test files, restricted to the feature's own slice. Requirement ids are per-feature
/// (every BRS starts at REQ-1), so scanning the whole workspace makes one feature's tests satisfy
/// another feature's requirements — both for the coverage check and the progress meter. Scoping
/// to the slice is what makes "covered" mean "covered by *this* feature's tests".
/// </summary>
public static class FeatureTestFiles
{
    public static IReadOnlyList<TestFileInfo> Discover(string workspacePath, string featureKey, SliceManifest? manifest)
    {
        var tests = TestDiscovery.Discover(workspacePath);
        var templates = SliceTemplates(manifest);
        if (templates is null)
            return tests;

        return tests
            .Where(test => SliceAllowlist.IsInSlice(test.Path, featureKey, templates))
            .ToArray();
    }

    /// <summary>
    /// The feature's slice templates, or null when there is nothing to scope by (no manifest, or a
    /// manifest that declares no code paths) — in which case callers fall back to the whole
    /// workspace rather than filtering everything out.
    /// </summary>
    public static IReadOnlyList<string>? SliceTemplates(SliceManifest? manifest)
    {
        if (manifest is null)
            return null;

        var templates = manifest.EffectiveCodePaths;
        return templates.Count == 0 ? null : templates;
    }
}
