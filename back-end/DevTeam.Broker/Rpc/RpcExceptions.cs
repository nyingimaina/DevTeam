using System.Text.Json;

namespace DevTeam.Broker.Rpc;

/// <summary>Raised when the peer answers a request with a JSON-RPC error object.</summary>
public sealed class RpcException : Exception
{
    public RpcException(string message, int code, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public int Code { get; }

    /// <summary>The optional <c>error.data</c> payload from the peer, if any.</summary>
    public JsonElement? ErrorData { get; }
}

/// <summary>Raised locally when the peer process disappears before answering.</summary>
public sealed class AcpDisconnectedException : IOException
{
    public AcpDisconnectedException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised locally when a request waits too long for its answer.</summary>
public sealed class AcpTimeoutException : TimeoutException
{
    public AcpTimeoutException(string method, TimeSpan elapsed)
        : base($"ACP request '{method}' did not complete within {elapsed.TotalSeconds:0.#}s")
    {
    }
}

/// <summary>
/// Raised when the agent accepted a prompt but then produced no output at all for long enough
/// that a healthy turn could not still be running (see BrokerCoordinator's stall watchdog).
/// Distinct from <see cref="AcpTimeoutException"/>: that is the overall request budget, while
/// this fires far sooner and means "the stream went silent", which is what a user experiences
/// as a hang.
/// </summary>
public sealed class AcpStalledException : TimeoutException
{
    public AcpStalledException(TimeSpan silentFor)
        : base($"The agent produced no output for {silentFor.TotalMinutes:0.#} minutes")
    {
    }
}

/// <summary>
/// Raised when the model provider itself refused the request — a rate limit, an unavailable
/// endpoint, an unknown model. Carries a sentence the user can act on, because the whole point is
/// to stop saying "the agent stopped responding" when the real answer is "the AI service is busy".
/// </summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException(string plainReason, string? modelId, bool isRateLimit)
        : base(plainReason)
    {
        PlainReason = plainReason;
        ModelId = modelId;
        IsRateLimit = isRateLimit;
    }

    public string PlainReason { get; }

    /// <summary>The model that was in effect when it failed, when we know it.</summary>
    public string? ModelId { get; }

    /// <summary>Rate limits clear on their own, so this one is worth retrying rather than escalating.</summary>
    public bool IsRateLimit { get; }
}