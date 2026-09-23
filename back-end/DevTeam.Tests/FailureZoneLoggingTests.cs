using DevTeam.Broker.Gates;
using Microsoft.Extensions.Logging;

namespace DevTeam.Tests;

/// <summary>
/// The failure zones must be diagnosable after the fact: which check ran, what it said, and what
/// command was executed with what result. These pin that a specialist gets real detail.
/// </summary>
public class FailureZoneLoggingTests
{
    [Fact]
    public async Task GateRunner_LogsTheReasonAndEvidenceWhenACheckFails()
    {
        var logger = new CapturingLogger<GateRunner>();
        var runner = new GateRunner([new StubGate(GateResult.Fail("Tests failed", "3 of 40 failed: CalculatorTests"))], logger);

        await runner.RunAsync("verify_code", new GateRequest("verify_code", @"C:\work\proj", "feat-1", "developer"), CancellationToken.None);

        var warning = Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("verify_code", warning.Message);
        Assert.Contains("Tests failed", warning.Message);
        Assert.Contains("CalculatorTests", warning.Message);
    }

    [Fact]
    public async Task GateRunner_LogsASuccessAtDebugOnly()
    {
        var logger = new CapturingLogger<GateRunner>();
        var runner = new GateRunner([new StubGate(GateResult.Pass("ok"))], logger);

        await runner.RunAsync("verify_code", new GateRequest("verify_code", @"C:\work\proj", "feat-1"), CancellationToken.None);

        Assert.False(logger.Has(LogLevel.Warning));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("passed"));
    }

    [Fact]
    public async Task GateRunner_LogsAThrowingCheckWithTheException()
    {
        var logger = new CapturingLogger<GateRunner>();
        var runner = new GateRunner([new ThrowingGate()], logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("verify_code", new GateRequest("verify_code", @"C:\work\proj", "feat-1"), CancellationToken.None));

        var error = Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Error));
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("verify_code", error.Message);
    }

    [Fact]
    public async Task ProcessRunner_LogsTheCommandAndItsOutputWhenItFails()
    {
        var logger = new CapturingLogger<SystemProcessRunner>();
        var runner = new SystemProcessRunner(logger);

        var result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", "/d /c exit 3", Path.GetTempPath()), CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        var warning = Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("exit 3", warning.Message);
        Assert.Contains("3", warning.Message);
    }

    [Fact]
    public async Task ProcessRunner_LogsTheCommandThatRan()
    {
        var logger = new CapturingLogger<SystemProcessRunner>();
        var runner = new SystemProcessRunner(logger);

        await runner.RunAsync(new ProcessRunRequest("cmd.exe", "/d /c echo hello", Path.GetTempPath()), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("echo hello"));
    }

    private sealed class StubGate(GateResult result) : IGate
    {
        public string Name => "verify_code";

        public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class ThrowingGate : IGate
    {
        public string Name => "verify_code";

        public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("gate blew up");
    }
}
