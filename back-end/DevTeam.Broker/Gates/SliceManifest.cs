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
        string? corePathFront = null)
    {
        Feature = feature;
        Title = title;
        CodePathBack = codePathBack;
        CodePathFront = codePathFront;
        Shared = shared.ToList();
        TestCommand = testCommand;
        CorePathBack = corePathBack ?? string.Empty;
        CorePathFront = corePathFront ?? string.Empty;
    }

    public string Feature { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string CodePathBack { get; set; } = string.Empty;

    public string CodePathFront { get; set; } = string.Empty;

    public List<string> Shared { get; set; } = [];

    public string TestCommand { get; set; } = "dotnet test DevTeam.slnx";

    // Paths to the shared core app this feature builds on top of; empty means the platform
    // defaults (see CorePaths). Serialized into the manifest so every gate and prompt resolves
    // the same core, and write access to it is granted to developer scopes.
    public string CorePathBack { get; set; } = string.Empty;

    public string CorePathFront { get; set; } = string.Empty;
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