namespace DevTeam.Broker.SemaNami;

/// <summary>
/// Which feature the single, MVP live SemaNami conversation is currently bound to, so an
/// incoming reply can be routed to the right feature's
/// SendMessageEnforcingSingleQuestionAsync call. True concurrent multi-release disambiguation
/// (more than one open conversation at once) is out of scope for this pass — SemaNami's own
/// un-threaded-reply resolution is genuinely ambiguous with more than one, short of the human
/// using Telegram's native reply-to feature.
/// </summary>
public sealed class SemaNamiChannelState
{
    // A single fixed conversation id is sufficient under the "one active conversation" MVP
    // assumption — a per-release id (e.g. "devteam-<releaseKey>") isn't needed until true
    // multi-release support is built, and StageOutcome doesn't carry the release key today
    // anyway (only the feature key/id).
    public const string ConversationId = "devteam";

    private readonly object _lock = new();
    private Guid? _boundFeatureId;

    public Guid? BoundFeatureId
    {
        get { lock (_lock) return _boundFeatureId; }
    }

    public void Bind(Guid featureId)
    {
        lock (_lock)
        {
            _boundFeatureId = featureId;
        }
    }
}
