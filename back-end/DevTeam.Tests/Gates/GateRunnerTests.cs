using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class GateRunnerTests
{
    private static GateRunner CreateRunner()
        => new(BuiltinGateRegistry.Create(new FakeProcessRunner(_ =>
            new ProcessRunResult(0, string.Empty, string.Empty, false, TimeSpan.Zero))));

    [Fact]
    public void Registry_CoversEveryKnownBuiltinExactlyOnce()
    {
        var gates = BuiltinGateRegistry.Create(new FakeProcessRunner(_ =>
            new ProcessRunResult(0, string.Empty, string.Empty, false, TimeSpan.Zero)));

        var gateNames = gates.Select(g => g.Name).ToHashSet();
        Assert.Equal(BuiltinRegistry.All, gateNames);
    }

    [Fact]
    public async Task UnknownBuiltinThrowsLoudly()
    {
        var gate = CreateRunner();

        var exception = await Assert.ThrowsAsync<UnknownGateException>(() =>
            gate.RunAsync("not_a_builtin", new GateRequest("not_a_builtin", @"C:\work\proj", "feat-001"), CancellationToken.None));

        Assert.Contains("not_a_builtin", exception.Message);
        Assert.Contains("Known builtins", exception.Message);
    }
}