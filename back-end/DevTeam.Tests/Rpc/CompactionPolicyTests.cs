using System.Diagnostics;
using System.Text.Json;
using DevTeam.Broker.Rpc;

namespace DevTeam.Tests.Rpc;

// Shares a collection with OpencodeAcpProcessLauncherTests: both mutate the process-wide
// OPENCODE_CONFIG_CONTENT value, and xUnit runs test classes in parallel by default.
[Collection(CompactionPolicyCollection.Name)]
public class CompactionPolicyTests
{
    [Fact]
    public void ConfigContent_EnablesPruningWithReservedHeadroom()
    {
        using var doc = JsonDocument.Parse(CompactionPolicy.ConfigContent);
        var compaction = doc.RootElement.GetProperty("compaction");

        Assert.True(compaction.GetProperty("prune").GetBoolean());
        Assert.Equal(CompactionPolicy.ReservedTokens, compaction.GetProperty("reserved").GetInt32());
    }

    [Fact]
    public void ConfigContent_ReservedIsLargeEnoughToCompactBeforeOverflow()
    {
        // A reserved buffer only earns its keep if it is a meaningful slice of a real context
        // window. Too small and "auto" still waits until the window is nearly full, which is the
        // behaviour this policy exists to replace.
        Assert.True(CompactionPolicy.ReservedTokens >= 16_000,
            $"reserved={CompactionPolicy.ReservedTokens} leaves too little headroom to compact early");
    }

    [Fact]
    public void Apply_SetsConfigContentOnChildEnvironment()
    {
        var startInfo = new ProcessStartInfo();

        CompactionPolicy.Apply(startInfo);

        Assert.Equal(CompactionPolicy.ConfigContent, startInfo.Environment[CompactionPolicy.EnvironmentVariable]);
    }

    [Fact]
    public void Apply_LeavesUnrelatedEnvironmentEntriesAlone()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["PATH"] = @"C:\keep\me";

        CompactionPolicy.Apply(startInfo);

        Assert.Equal(@"C:\keep\me", startInfo.Environment["PATH"]);
    }

    [Fact]
    public void Apply_KeepsAnExplicitUserSuppliedConfigContent()
    {
        // The child inherits the broker's environment, so a user who deliberately set their own
        // OPENCODE_CONFIG_CONTENT would otherwise have it silently replaced on every launch.
        var startInfo = new ProcessStartInfo();
        startInfo.Environment[CompactionPolicy.EnvironmentVariable] = """{"theme":"dark"}""";

        CompactionPolicy.Apply(startInfo);

        Assert.Equal("""{"theme":"dark"}""", startInfo.Environment[CompactionPolicy.EnvironmentVariable]);
        Assert.False(CompactionPolicy.TryApply(startInfo, out var stillOurs));
        Assert.Equal("""{"theme":"dark"}""", stillOurs);
    }

    [Fact]
    public void TryApply_ReportsWhetherThePolicyOwnsTheConfigContent()
    {
        var startInfo = new ProcessStartInfo();

        Assert.True(CompactionPolicy.TryApply(startInfo, out var configContent));
        Assert.Equal(CompactionPolicy.ConfigContent, configContent);
    }
}
