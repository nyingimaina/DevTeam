using DevTeam.Broker.Server;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// Pairs a prompt with the feature-boundary reset it implies.
/// </summary>
/// <remarks>
/// A session belongs to a workspace rather than a feature, so the agent's context outlives the
/// feature it was built for. Discarding that context is only safe <em>at</em> a feature change,
/// and doing it in the same step as the prompt is what makes it impossible to forget: the reset and
/// the prompt that needs a clean context are one call, so a new call site cannot accidentally
/// inherit the previous feature's conversation.
/// </remarks>
public static class FeatureContextBoundary
{
    public static async Task<PromptResponse> PromptForFeatureAsync(
        this IWorkflowCoordinator coordinator,
        Guid sessionId,
        string featureKey,
        string text,
        CancellationToken ct,
        bool isPriming = false,
        string? displayText = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        await coordinator.ResetContextOnFeatureChangeAsync(sessionId, featureKey, ct);
        return await coordinator.PromptWithSessionRecoveryAsync(
            sessionId, text, ct, isPriming, displayText);
    }

    /// <summary>
    /// Resets the context at a feature change and compacts it if it is nearly full, then prompts.
    /// </summary>
    /// <remarks>
    /// The two act in that order deliberately. A reset only happens when the feature actually
    /// changed, so on a normal follow-up step the reset is a no-op and the context is whatever the
    /// previous steps left behind — which, on a long stage, is often most of the window. Compacting
    /// after the reset (rather than instead of it) means a feature change gets a genuinely empty
    /// context, while a same-feature step gets one that is merely no longer full.
    ///
    /// The decision is the policy's, and it refuses when a turn is running, when the context has not
    /// been measured, or when a recent compaction has not been refilled enough to justify a second
    /// paid call. Every refusal carries its reason so "the bar never filled and the agent still ran
    /// out" is answerable afterwards.
    /// </remarks>
    public static async Task<PromptResponse> PromptForFeatureWithAutoCompactAsync(
        this IWorkflowCoordinator coordinator,
        Guid sessionId,
        string featureKey,
        string text,
        CancellationToken ct,
        bool isPriming = false,
        string? displayText = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        await coordinator.ResetContextOnFeatureChangeAsync(sessionId, featureKey, ct);

        await CompactIfNearlyFullAsync(coordinator, sessionId, ct);

        return await coordinator.PromptWithSessionRecoveryAsync(
            sessionId, text, ct, isPriming, displayText);
    }

    private static async Task CompactIfNearlyFullAsync(
        IWorkflowCoordinator coordinator, Guid sessionId, CancellationToken ct)
    {
        if (coordinator is not BrokerCoordinator broker)
            return;

        var context = broker.CurrentContext();
        var decision = ContextCompactionPolicy.Decide(broker.CompactionPolicy, context, context.TurnActive);
        if (!decision.ShouldCompact)
        {
            broker.NoteAutoCompactionSkipped(decision.SkipReason);
            return;
        }

        var result = await broker.CompactContextAsync(sessionId, ct);
        if (result is null)
            return;

        broker.CompactionPolicy = broker.CompactionPolicy.AfterCompaction(result.UsedTokensAfter);
        broker.NoteAutoCompactionRan(decision.Occupancy, result);
    }

    /// <summary>
    /// The prompt that asks the agent to summarize its context and make room.
    /// </summary>
    /// <remarks>
    /// This is the agent's own compaction, reached through an ordinary prompt — opencode routes a
    /// prompt starting with this to its summarization. That is deliberately different from a
    /// boundary reset, which throws the session away: compaction keeps whatever the agent worked
    /// out and only shortens how it is represented, so a user who asks to compact does not silently
    /// lose the work.
    /// </remarks>
    public const string CompactPromptText = "/compact";
}
