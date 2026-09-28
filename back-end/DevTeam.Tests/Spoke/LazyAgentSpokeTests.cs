using DevTeam.Broker.Spoke;

namespace DevTeam.Tests.Spoke;

/// <summary>
/// The agent spoke sits on the dependency chain of the workflow engine, which every release and
/// feature endpoint resolves. Constructing the real spoke starts <c>opencode acp</c>, so a spoke
/// built while a read-only request is in flight makes an unrelated endpoint fail whenever the
/// agent cannot be launched. These tests pin the deferral: nothing is created until an agent call
/// is actually made.
/// </summary>
public sealed class LazyAgentSpokeTests
{
    [Fact]
    public async Task Construction_DoesNotCreateTheInnerSpoke()
    {
        var created = 0;
        var lazy = new LazyAgentSpoke(() =>
        {
            created++;
            return new FakeSpoke();
        });

        await Task.CompletedTask;

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task FirstAgentCall_CreatesTheInnerSpokeExactlyOnce()
    {
        var created = 0;
        var lazy = new LazyAgentSpoke(() =>
        {
            created++;
            return new FakeSpoke();
        });

        await lazy.InitializeAsync(CancellationToken.None);
        await lazy.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, created);
    }

    [Fact]
    public async Task ConcurrentFirstCalls_CreateTheInnerSpokeOnlyOnce()
    {
        var created = 0;
        var gate = new TaskCompletionSource();
        var lazy = new LazyAgentSpoke(() =>
        {
            Interlocked.Increment(ref created);
            // Hold the factory open so both callers genuinely overlap.
            gate.Task.GetAwaiter().GetResult();
            return new FakeSpoke();
        });

        var first = Task.Run(() => lazy.InitializeAsync(CancellationToken.None));
        var second = Task.Run(() => lazy.NewSessionAsync("C:\\work", CancellationToken.None));
        await Task.Delay(100);
        gate.SetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, created);
    }

    [Fact]
    public async Task FactoryFailure_SurfacesOnTheAgentCall_NotOnConstruction()
    {
        var lazy = new LazyAgentSpoke(() =>
            throw new InvalidOperationException("opencode executable not found"));

        // Construction is the part a read-only endpoint pays; it must not throw.
        await Task.CompletedTask;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lazy.InitializeAsync(CancellationToken.None));
        Assert.Contains("opencode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentCalls_AreForwardedToTheInnerSpoke()
    {
        var inner = new FakeSpoke();
        var lazy = new LazyAgentSpoke(() => inner);

        var info = await lazy.InitializeAsync(CancellationToken.None);
        var session = await lazy.NewSessionAsync("C:\\work", CancellationToken.None);
        await lazy.SetModelAsync(session.SessionId, "model-x", CancellationToken.None);
        await lazy.SetModeAsync(session.SessionId, "mode-y", CancellationToken.None);
        await lazy.CancelAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(1, inner.InitializeCalls);
        Assert.Equal("C:\\work", inner.LastCwd);
        Assert.Equal("model-x", inner.LastModelId);
        Assert.Equal("mode-y", inner.LastModeId);
        Assert.Equal(session.SessionId, inner.LastCancelledSessionId);
        Assert.Equal("FakeAgent", info.Name);
    }

    [Fact]
    public async Task EventReceived_IsForwardedFromTheInnerSpoke()
    {
        var inner = new FakeSpoke();
        var lazy = new LazyAgentSpoke(() => inner);

        AgentEvent? received = null;
        lazy.EventReceived += (_, e) => received = e;

        await lazy.InitializeAsync(CancellationToken.None);
        inner.Raise();

        Assert.NotNull(received);
    }

    [Fact]
    public void Dispose_DisposesTheInnerSpoke_AndIsSafeWhenNeverMaterialized()
    {
        var unused = new LazyAgentSpoke(() => new FakeSpoke());
        unused.Dispose();

        var inner = new FakeSpoke();
        var used = new LazyAgentSpoke(() => inner);
        used.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        used.Dispose();

        Assert.Equal(1, inner.DisposeCalls);
    }

    private sealed class FakeSpoke : IAgentSpoke
    {
        public int InitializeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public string? LastCwd { get; private set; }

        public string? LastModelId { get; private set; }

        public string? LastModeId { get; private set; }

        public string? LastCancelledSessionId { get; private set; }

        public event EventHandler<AgentEvent>? EventReceived;

        public Task<AgentInfo> InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCalls++;
            return Task.FromResult(new AgentInfo("FakeAgent", "9.9.9"));
        }

        public Task<AgentSession> NewSessionAsync(string cwd, CancellationToken cancellationToken)
        {
            LastCwd = cwd;
            return Task.FromResult(new AgentSession("ses_1", []));
        }

        public Task<AgentPromptResult> PromptAsync(
            string sessionId,
            IReadOnlyList<AgentPromptPart> prompt,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentPromptResult("end_turn", null, null));

        public Task SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken)
        {
            LastModelId = modelId;
            return Task.CompletedTask;
        }

        public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
        {
            LastModeId = modeId;
            return Task.CompletedTask;
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
        {
            LastCancelledSessionId = sessionId;
            return Task.CompletedTask;
        }

        public void Raise() =>
            EventReceived?.Invoke(this, new AgentTextDelta("ses_1", "msg_1", "hello"));

        public void Dispose() => DisposeCalls++;
    }
}
