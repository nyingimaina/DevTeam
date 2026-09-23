using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevTeam.Broker.Gates;

public sealed class SliceManifest
{
    public SliceManifest()
    {
    }

    public SliceManifest(
        string feature,
        string title,
        string codePathBack,
        string codePathFront,
        IReadOnlyList<string> shared,
        string testCommand,
        string? corePathBack = null,
        string? corePathFront = null,
        string? runCommand = null)
    {
        Feature = feature;
        Title = title;
        CodePathBack = codePathBack;
        CodePathFront = codePathFront;
        Shared = shared.ToList();
        TestCommand = testCommand;
        CorePathBack = corePathBack ?? string.Empty;
        CorePathFront = corePathFront ?? string.Empty;
        RunCommand = runCommand;
    }

    // Free-form alternative to the fixed backend/frontend split above — for an app that doesn't
    // naturally split that way (a single WPF/console/library project), or one whose hierarchy the
    // business-analyst and user agreed on directly. CodePathBack/CodePathFront are still populated
    // (best-effort, from the first two entries) purely so existing display text and the DB
    // columns that mirror them keep working; EffectiveCodePaths is what callers should read.
    public SliceManifest(
        string feature,
        string title,
        IReadOnlyList<string> codePaths,
        IReadOnlyList<string> shared,
        string testCommand,
        string? corePathBack = null,
        string? corePathFront = null,
        string? runCommand = null)
        : this(
            feature, title,
            codePaths.Count > 0 ? codePaths[0] : string.Empty,
            codePaths.Count > 1 ? codePaths[1] : string.Empty,
            shared, testCommand, corePathBack, corePathFront, runCommand)
    {
        CodePaths = codePaths.ToList();
    }

    public string Feature { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string CodePathBack { get; set; } = string.Empty;

    public string CodePathFront { get; set; } = string.Empty;

    // Free-form list of code path templates. Empty on a manifest scaffolded (or hand-authored)
    // before this existed, or one that never needed more than the classic back/front split —
    // EffectiveCodePaths falls back to CodePathBack/CodePathFront in that case.
    public List<string> CodePaths { get; set; } = [];

    // What every gate/prompt that needs "this feature's code path templates" should read,
    // regardless of whether the manifest used the free-form list or the classic two fields.
    public IReadOnlyList<string> EffectiveCodePaths =>
        CodePaths.Count > 0
            ? CodePaths
            : new[] { CodePathBack, CodePathFront }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

    public List<string> Shared { get; set; } = [];

    public string TestCommand { get; set; } = "dotnet test DevTeam.slnx";

    // Optional command that starts the built app, used by the launch smoke check. Empty means
    // "discover the app executable from the built output" (see AppLaunchGate).
    public string? RunCommand { get; set; }

    // Paths to the shared core app this feature builds on top of; empty means the platform
    // defaults (see CorePaths). Serialized into the manifest so every gate and prompt resolves
    // the same core, and write access to it is granted to developer scopes.
    public string CorePathBack { get; set; } = string.Empty;

    public string CorePathFront { get; set; } = string.Empty;

    // Free-form list of core paths — for a shared core that doesn't split into exactly a "back"
    // and a "front" either (same idea as CodePaths/EffectiveCodePaths above). Empty on a
    // manifest that never needed more than the classic pair; EffectiveCorePaths falls back to
    // CorePathBack/CorePathFront in that case.
    public List<string> CorePaths { get; set; } = [];

    public IReadOnlyList<string> EffectiveCorePaths =>
        CorePaths.Count > 0
            ? CorePaths
            : new[] { CorePathBack, CorePathFront }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
}

public static class SliceManifestIO
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static string DefaultPath(string workspacePath, string featureKey)
        => Path.Combine(workspacePath, "devteam", "features", featureKey, "manifest.yaml");

    public static string Write(string path, SliceManifest manifest)
    {
        if (File.Exists(path))
            throw new IOException($"Manifest already exists: {path}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serializer.Serialize(manifest));
        return path;
    }

    public static string Save(string path, SliceManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serializer.Serialize(manifest));
        return path;
    }

    public static SliceManifest? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        try
        {
            return deserializer.Deserialize<SliceManifest>(File.ReadAllText(path));
        }
        catch (YamlException)
        {
            // The manifest is hand-editable by any role's agent (it lives under the feature's
            // own artifacts directory), so malformed YAML on disk is an expected failure mode,
            // not a bug — treat it the same as "no manifest yet" rather than crashing the caller.
            return null;
        }
    }
}

public static class GateInputs
{
    public static string Get(IReadOnlyDictionary<string, string>? inputs, string key, string fallback = "")
        => inputs is not null && inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    public static string? GetOptional(IReadOnlyDictionary<string, string>? inputs, string key)
        => inputs is not null && inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public static IReadOnlyList<string> GetList(IReadOnlyDictionary<string, string>? inputs, string key)
    {
        var raw = GetOptional(inputs, key);
        return raw is null
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}