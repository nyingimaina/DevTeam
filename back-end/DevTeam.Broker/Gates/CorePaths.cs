using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Resolves the shared-core directories for a release's workspace. Every feature slice builds
/// additively on a single shared core app (the base project / scaffold that ships once and is
/// reused by all features) so features stop duplicating whole apps per feature. Core paths may
/// be configured per-release in the workflow's <c>coreBack</c>/<c>coreFront</c> slice fields or
/// come from the feature manifest; defaults follow the platform convention
/// (<c>back-end/src/Core</c>, <c>front-end/app/core</c>). Write access to the core is granted
/// to developer scopes so features can extend it, and read access is surfaced to agents via the
/// core code map in <see cref="ContextBundleGate"/>.
/// </summary>
public static class CorePaths
{
    public const string DefaultBack = "back-end/src/Core";
    public const string DefaultFront = "front-end/app/core";

    public static string Back(WorkflowSlices slices) => Defaults(slices.CoreBack, DefaultBack);

    public static string Front(WorkflowSlices slices) => Defaults(slices.CoreFront, DefaultFront);

    public static string Back(SliceManifest? manifest)
        => string.IsNullOrWhiteSpace(manifest?.CorePathBack) ? DefaultBack : manifest!.CorePathBack;

    public static string Front(SliceManifest? manifest)
        => string.IsNullOrWhiteSpace(manifest?.CorePathFront) ? DefaultFront : manifest!.CorePathFront;

    public static (string Back, string Front) Resolve(WorkflowSlices slices, SliceManifest? manifest)
        => (manifest is { } m && !string.IsNullOrWhiteSpace(m.CorePathBack) ? m.CorePathBack : Back(slices),
            manifest is { } m2 && !string.IsNullOrWhiteSpace(m2.CorePathFront) ? m2.CorePathFront : Front(slices));

    // The list-returning shape a caller that doesn't want to special-case exactly two named
    // slots (ContextBundleGate, CoreReuseChecker) should use — same precedence as the tuple
    // form above, just generalized. Back/Front always resolve to something non-empty (there's
    // always a default), so this is currently always a 2-element list.
    public static IReadOnlyList<string> ResolveList(WorkflowSlices slices, SliceManifest? manifest)
    {
        var (back, front) = Resolve(slices, manifest);
        return new[] { back, front }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    private static string Defaults(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured!;
}