namespace DevTeam.Broker.Server;

/// <summary>
/// One thing the agent did, in plain language, for the live activity feed. Held in memory for the
/// duration of a turn only: this answers "what is happening right now", not "what happened"
/// (history lives in the stage-run ledger).
/// </summary>
public sealed record TurnActivityEntry(
    DateTimeOffset At,
    string Kind,
    string Label,
    string? Detail = null,
    string? Status = null);

public static class TurnActivityKind
{
    /// <summary>A tool the agent used (reading, editing, running a command).</summary>
    public const string Tool = "tool";

    /// <summary>A short snippet of what the agent just said.</summary>
    public const string Text = "text";

    /// <summary>A short snippet of the agent's private reasoning — only shown when the user opts in.</summary>
    public const string Thought = "thought";

    /// <summary>A turn boundary (started / finished / failed).</summary>
    public const string Status = "status";
}
