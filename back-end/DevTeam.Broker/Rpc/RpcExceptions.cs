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