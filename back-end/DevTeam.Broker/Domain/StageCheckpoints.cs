using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevTeam.Broker.Domain;

/// <summary>
/// Which phase of a stage's turn the latest eagerly-persisted checkpoint reflects. The
/// startup crash-recoverer uses it to distinguish "the last agent turn finished fine" from
/// "the broker died mid-prompt" — the difference between leaving a run Active and surfacing it
/// as Escalated so the human knows the ACP session it was talking to is gone.
/// </summary>
public enum StageCheckpointSignal
{
    /// <summary>A freshly created run that has not started its first prompt yet.</summary>
    Idle,
    /// <summary>A prompt was dispatched to the agent and has not completed — crash here means the turn is lost.</summary>
    PromptInProgress,
    /// <summary>The last prompt completed and its result was persisted — a crash here is safe to ignore.</summary>
    PromptSucceeded,
    /// <summary>The run is in (or was in) its gates phase; <see cref="StageCheckpoint.Steps"/> is the step ledger.</summary>
    Gates,
}

/// <summary>One entry in a stage's gate-step ledger, flushed eagerly so a mid-gates crash leaves a visible trail.</summary>
public sealed record CheckpointStepResult(string Name, bool Passed, string? Reason);

/// <summary>
/// The serializable progress marker a StageRun writes to disk (ReleaseStageRun.CheckpointJson)
/// before and after every agent turn and gate step. Plain JSON on a string column so the
/// schema-sync pass can add it to pre-existing databases without a migration row.
/// </summary>
public sealed record StageCheckpoint(
    StageCheckpointSignal Signal,
    StagePhase Phase,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CheckpointStepResult>? Steps = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    public static StageCheckpoint? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<StageCheckpoint>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            // A half-written or corrupted checkpoint (e.g. from a crash mid-serialize) is
            // treated as "unknown" — callers must then assume the turn was interrupted.
            return null;
        }
    }
}