namespace DevTeam.Broker.Rpc;

/// <summary>
/// A live child-process link for ACP JSON-RPC over stdio: read/write newline-delimited
/// JSON frames and tear the child down.
/// </summary>
public interface IAcpProcess : IDisposable
{
    /// <summary>OS process id of the running agent host, for exclusion from workspace process sweeps.</summary>
    int ProcessId { get; }

    /// <summary>Reads the next line of output, or null on EOF/process exit.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Writes a single line of input, followed by a newline.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    void Kill();
}