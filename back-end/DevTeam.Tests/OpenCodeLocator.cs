using DevTeam.Shared;

namespace DevTeam.Tests;

/// <summary>
/// Locates the <c>opencode</c> executable for tests that talk to the real agent, skipping portably
/// when it is absent. This deliberately delegates to the production resolver: when the two
/// disagreed, the integration tests found a CLI on PATH that the shipped app could not see.
/// </summary>
internal static class OpenCodeLocator
{
    public static string? ResolvePath() =>
        OpenCodePathResolver.Resolve(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("OPENCODE_PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
}