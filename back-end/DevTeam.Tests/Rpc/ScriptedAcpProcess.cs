using System.Text;
using DevTeam.Broker.Rpc;

namespace DevTeam.Tests.Rpc;

/// <summary>
/// Fakes the ACP child process over in-memory pipes: <see cref="ReadLineAsync"/>
/// surfaces whatever the test writes via <see cref="AcpPipeHarness.Emit"/> and
/// <see cref="WriteLineAsync"/> delivers client frames back to the harness's
/// read stream.
/// </summary>
internal sealed class ScriptedAcpProcess : IAcpProcess
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();

    public ScriptedAcpProcess(Stream input, Stream output)
    {
        _reader = new StreamReader(input, new UTF8Encoding(false));
        _writer = new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public int ProcessId => -1;

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => _reader.ReadLineAsync(cancellationToken).AsTask();

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        lock (_writeLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.Write(line);
            _writer.Write('\n');
            _writer.Flush();
        }

        return Task.CompletedTask;
    }

    public void Kill()
    {
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
    }
}