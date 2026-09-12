using System.Text.Json;

namespace DevTeam.Broker.Spoke;

/// <summary>Outcome of a permission request presented to the broker's policy engine.</summary>
public enum PermissionVerdict
{
    Reject,

    /// <summary>Allow this exact tool invocation once.</summary>
    AllowOnce,

    /// <summary>Remember the grant for this tool for the rest of the session.</summary>
    AllowAlways,
}

public sealed record PermissionRequest(
    string SessionId,
    string ToolCallId,
    string? Title,
    JsonElement? RawInput);

/// <summary>
/// Decides whether a tool invocation the agent is about to run is permitted.
/// The broker replaces ACP's default auto-reject with this hook, so a future UI
/// can surface a real allow/reject prompt without changing the transport.
/// </summary>
public interface IPermissionPolicy
{
    Task<PermissionVerdict> DecideAsync(PermissionRequest request, CancellationToken cancellationToken);
}

/// <summary>The safe default: block anything that can be blocked.</summary>
public sealed class RejectAllPermissionPolicy : IPermissionPolicy
{
    public Task<PermissionVerdict> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PermissionVerdict.Reject);
}