namespace DevTeam.Tests;

/// <summary>
/// Serializes the test classes that mutate the process-wide <c>OPENCODE_CONFIG_CONTENT</c>
/// environment variable. xUnit runs test classes in parallel by default, so without a shared
/// collection these classes would overwrite each other's value mid-test.
/// </summary>
public static class CompactionPolicyCollection
{
    public const string Name = "compaction-policy";
}
