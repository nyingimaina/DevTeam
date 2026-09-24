using DevTeam.Broker.Domain;
using DevTeam.Broker.SemaNami;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SemaNami.Core.Conversations;

namespace DevTeam.Tests.SemaNami;

public class SemaNamiReplyRouterTests
{
    private sealed class FakeWorkflowEngine : IWorkflowEngine
    {
        public List<(Guid FeatureId, string Text)> RoutedMessages { get; } = [];
        public Exception? ThrowOnSend { get; set; }

        public Task<StagePromptResult> SendMessageEnforcingSingleQuestionAsync(Guid featureId, string text, CancellationToken ct)
        {
            if (ThrowOnSend is not null)
                throw ThrowOnSend;

            RoutedMessages.Add((featureId, text));
            return Task.FromResult(new StagePromptResult("end_turn", 0, 0, 0));
        }

        // Not exercised by SemaNamiReplyRouter — this fake exists only to satisfy the interface.
        public Task<DevTeamRelease> StartReleaseAsync(string featureKey, string workspacePath, CancellationToken ct) => throw new NotImplementedException();
        public Task<ReleaseFeature> CreateFeatureAsync(Guid releaseId, string featureKey, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> SwitchFeatureAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> AdvanceAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ReleaseStageRun> StartStageAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<StagePromptResult> SendMessageAsync(Guid featureId, string text, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> RunGatesAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> ReopenBlockedGateAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> RunStageAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> PushBackAsync(Guid featureId, string targetStageName, string? instructions, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MessageDto>> GetStageMessagesAsync(Guid featureId, Guid stageRunId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<PipelineStageDto>> GetPipelineAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<StageArtifactDto>> GetStageArtifactsAsync(Guid featureId, Guid stageRunId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetWorkspaceChangesAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> SignoffAsync(Guid featureId, string stageName, string role, string? comment, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> RetryStageAsync(Guid featureId, string? targetStageName, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> RetryFeatureFinalizationAsync(Guid featureId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> GetReleaseAsync(Guid releaseId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> SetReleaseAutonomousEnabledAsync(Guid releaseId, bool enabled, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> FinalizeReleaseAsync(Guid releaseId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<DevTeamRelease>> ListReleasesAsync(string? workspacePath, CancellationToken ct) => throw new NotImplementedException();
        public Task<ReleaseFeature> StartHotfixAsync(string key, string workspacePath, CancellationToken ct) => throw new NotImplementedException();
        public Task<DevTeamRelease> FinalizeHotfixAsync(Guid hotfixId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<DevTeamRelease>> ListHotfixesAsync(string? workspacePath, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ModelOption>> GetAvailableModelsAsync(Guid releaseId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static (SemaNamiReplyRouter Router, FakeWorkflowEngine Engine, SemaNamiChannelState ChannelState) Create()
    {
        var engine = new FakeWorkflowEngine();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowEngine>(engine);
        var provider = services.BuildServiceProvider();
        var channelState = new SemaNamiChannelState();
        var router = new SemaNamiReplyRouter(channelState, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SemaNamiReplyRouter>.Instance);
        return (router, engine, channelState);
    }

    private static StoredMessage Message(string text) => new(1, "in", 42, text, false, DateTime.UtcNow);

    [Fact]
    public async Task RouteAsync_NoFeatureBoundYet_DoesNothing()
    {
        var (router, engine, _) = Create();

        await router.RouteAsync(Message("some reply"), CancellationToken.None);

        Assert.Empty(engine.RoutedMessages);
    }

    [Fact]
    public async Task RouteAsync_FeatureBound_SendsTheReplyToThatFeature()
    {
        var (router, engine, channelState) = Create();
        var featureId = Guid.NewGuid();
        channelState.Bind(featureId);

        await router.RouteAsync(Message("It's a WPF app, no backend/frontend split"), CancellationToken.None);

        var routed = Assert.Single(engine.RoutedMessages);
        Assert.Equal(featureId, routed.FeatureId);
        Assert.Equal("It's a WPF app, no backend/frontend split", routed.Text);
    }

    [Fact]
    public async Task RouteAsync_EngineThrows_DoesNotPropagate()
    {
        var (router, engine, channelState) = Create();
        channelState.Bind(Guid.NewGuid());
        engine.ThrowOnSend = new InvalidOperationException("stage is not accepting messages right now");

        var exception = await Record.ExceptionAsync(() => router.RouteAsync(Message("reply"), CancellationToken.None));

        Assert.Null(exception);
    }
}
