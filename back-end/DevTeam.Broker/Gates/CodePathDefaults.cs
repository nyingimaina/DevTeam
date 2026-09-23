namespace DevTeam.Broker.Gates;

/// <summary>
/// The platform's default classic backend/frontend code path templates — the single source of
/// truth for a default that used to be duplicated as literal strings in both
/// <see cref="Workflow.SlicesYaml"/> and <see cref="ScaffoldSpecsGate"/>, and could silently
/// drift out of sync. See <see cref="CorePaths"/> for the equivalent on the shared-core side.
/// </summary>
public static class CodePathDefaults
{
    public const string DefaultBack = "back-end/**/Features/<F>";
    public const string DefaultFront = "front-end/app/<F>";
}
