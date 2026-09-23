using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevTeam.Broker.Gates;

/// <summary>
/// A workspace-wide project profile — machine-written once (see ScaffoldSpecsGate) when the
/// first feature's business-analyst conversation settles on an app type and code hierarchy that
/// isn't the classic backend/frontend split. Downstream consumers (readiness detection,
/// core-path resolution) read it instead of re-guessing via directory/glob conventions.
/// Sibling to devteam/readiness.yaml, which — when present — still wins outright over this.
/// </summary>
public sealed class ProjectProfile
{
    public string ProjectType { get; set; } = string.Empty;

    public List<string> CorePaths { get; set; } = [];

    public string? BuildCommand { get; set; }

    public string? TestCommand { get; set; }
}

public static class ProjectProfileIO
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static string ProfilePath(string workspacePath)
        => Path.Combine(workspacePath, "devteam", "project-profile.yaml");

    public static bool Exists(string workspacePath) => File.Exists(ProfilePath(workspacePath));

    public static string Write(string workspacePath, ProjectProfile profile)
    {
        var path = ProfilePath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serializer.Serialize(profile));
        return path;
    }

    // Missing or malformed both resolve to "no profile" — callers soft-degrade to
    // convention-sniffing rather than treating a broken file as a hard failure (same treatment
    // SliceManifestIO.TryRead gives a hand-edited/corrupted manifest).
    public static ProjectProfile? TryRead(string workspacePath)
    {
        var path = ProfilePath(workspacePath);
        if (!File.Exists(path))
            return null;

        try
        {
            return Deserializer.Deserialize<ProjectProfile>(File.ReadAllText(path));
        }
        catch (YamlException)
        {
            return null;
        }
    }
}
