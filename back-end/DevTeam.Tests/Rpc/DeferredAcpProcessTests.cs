using DevTeam.Broker.Rpc;

namespace DevTeam.Tests.Rpc;

/// <summary>
/// The ACP process is a singleton whose factory launches <c>opencode acp</c>. Anything that
/// resolves it — directly, or through a constructor — would otherwise spawn an agent process
/// merely by being wired up. These tests pin that the launch is deferred to first real use, and
/// that asking for the pid (which the workspace process sweep does) never launches anything.
/// </summary>
public sealed class DeferredAcpProcessTests
{
    [Fact]
    public async Task Construction_DoesNotInvokeTheFactory()
    {
        var created = 0;
        var deferred = new DeferredAcpProcess(() =>
        {
            created++;
            return new FakeAcpProcess();
        });

        await Task.CompletedTask;

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task ProcessId_DoesNotLaunchTheProcess_AndReportsZero()
    {
        var created = 0;
        var deferred = new DeferredAcpProcess(() =>
        {
            created++;
            return new FakeAcpProcess { ProcessId = 4242 };
        });

        // The workspace process sweep only wants to know which pid to exclude; it must never be
        // the thing that starts the agent.
        Assert.Equal(0, deferred.ProcessId);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task FirstWrite_LaunchesTheProcessOnce_AndForwardsTheFrame()
    {
        var created = 0;
        FakeAcpProcess? inner = null;
        var deferred = new DeferredAcpProcess(() =>
        {
            created++;
            inner = new FakeAcpProcess();
            return inner;
        });

        await deferred.WriteLineAsync("{\"jsonrpc\":\"2.0\"}", CancellationToken.None);
        await deferred.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2}", CancellationToken.None);

        Assert.Equal(1, created);
        Assert.Equal(["{\"jsonrpc\":\"2.0\"}", "{\"jsonrpc\":\"2.0\",\"id\":2}"], inner!.Written);
    }

    [Fact]
    public async Task FirstRead_LaunchesTheProcess_AndReturnsItsLine()
    {
        FakeAcpProcess? inner = null;
        var deferred = new DeferredAcpProcess(() => inner = new FakeAcpProcess { NextLine = "hello" });

        var line = await deferred.ReadLineAsync(CancellationToken.None);

        Assert.Equal("hello", line);
        Assert.NotNull(inner);
    }

    [Fact]
    public async Task ProcessId_AfterLaunch_ReportsTheRunningPid()
    {
        var deferred = new DeferredAcpProcess(() => new FakeAcpProcess { ProcessId = 4242 });

        await deferred.WriteLineAsync("{}", CancellationToken.None);

        Assert.Equal(4242, deferred.ProcessId);
    }

    [Fact]
    public async Task LaunchFailure_SurfacesOnUse_NotOnConstruction()
    {
        var deferred = new DeferredAcpProcess(() =>
            throw new InvalidOperationException("opencode executable not found"));

        await Task.CompletedTask;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => deferred.ReadLineAsync(CancellationToken.None));
        Assert.Contains("opencode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kill_BeforeAnyUse_DoesNotLaunchTheProcess()
    {
        var created = 0;
        var deferred = new DeferredAcpProcess(() =>
        {
            created++;
            return new FakeAcpProcess();
        });

        deferred.Kill();

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Kill_AfterUse_ReachesTheRunningProcess()
    {
        var inner = new FakeAcpProcess();
        var deferred = new DeferredAcpProcess(() => inner);

        await deferred.WriteLineAsync("{}", CancellationToken.None);
        deferred.Kill();

        Assert.Equal(1, inner.KillCalls);
    }

    [Fact]
    public async Task Dispose_DisposesTheRunningProcess_AndIsSafeWhenNeverLaunched()
    {
        var unused = new DeferredAcpProcess(() => new FakeAcpProcess());
        unused.Dispose();

        var inner = new FakeAcpProcess();
        var used = new DeferredAcpProcess(() => inner);
        await used.WriteLineAsync("{}", CancellationToken.None);
        used.Dispose();

        Assert.Equal(1, inner.DisposeCalls);
    }

    private sealed class FakeAcpProcess : IAcpProcess
    {
        public int ProcessId { get; init; }

        public string? NextLine { get; init; }

        public List<string> Written { get; } = [];

        public int KillCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextLine);

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            Written.Add(line);
            return Task.CompletedTask;
        }

        public void Kill() => KillCalls++;

        public void Dispose() => DisposeCalls++;
    }
}
