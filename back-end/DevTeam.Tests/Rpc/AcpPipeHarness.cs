using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DevTeam.Tests.Rpc;

/// <summary>
/// Drives a <see cref="ScriptedAcpProcess"/>: scripts server->client frames via
/// <see cref="Emit"/>, and reads client->server requests so tests can answer them.
/// </summary>
internal sealed class AcpPipeHarness : IDisposable
{
    private readonly AnonymousPipeServerStream _serverToClient; // test writes, connection reads
    private readonly AnonymousPipeServerStream _serverFromClient; // connection writes, test reads
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;

    public ScriptedAcpProcess Process { get; }

    public AcpPipeHarness()
    {
        _serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientInput = new AnonymousPipeClientStream(PipeDirection.In, _serverToClient.ClientSafePipeHandle);

        _serverFromClient = new AnonymousPipeServerStream(PipeDirection.In);
        var clientOutput = new AnonymousPipeClientStream(PipeDirection.Out, _serverFromClient.ClientSafePipeHandle);

        _writer = new StreamWriter(_serverToClient, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        _reader = new StreamReader(_serverFromClient, new UTF8Encoding(false));

        Process = new ScriptedAcpProcess(clientInput, clientOutput);
    }

    public void Emit(string jsonFrame) => _writer.WriteLine(jsonFrame);

    /// <summary>Signals an EOF from the fake agent (equivalent to the child exiting).</summary>
    public void SimulateProcessExit() => _serverToClient.Dispose();

    /// <summary>Reads the next client->server frame. Throws if the pipe closes first.</summary>
    public async Task<string> ReadClientFrameAsync(CancellationToken ct = default)
    {
        var line = await _reader.ReadLineAsync(ct);
        return line ?? throw new InvalidOperationException("client->server pipe closed before a frame arrived");
    }

    /// <summary>Reads the next client->server request (has jsonrpc+method+id).</summary>
    public async Task<(string Method, string IdRaw, string ParamsJson)> ReadRequestAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await ReadClientFrameAsync(ct);
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method))
                continue;

            return (
                method.GetString() ?? string.Empty,
                root.TryGetProperty("id", out var id) ? id.GetRawText() : string.Empty,
                root.TryGetProperty("params", out var parameters)
                    ? parameters.GetRawText()
                    : "{}");
        }
    }

    /// <summary>Answers an already-read request id with <paramref name="resultJson"/>.</summary>
    public void Reply(string idRaw, string resultJson)
        => _writer.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}");

    /// <summary>An error-shaped response.</summary>
    public void ReplyError(string idRaw, int code, string message)
        => _writer.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}");

    /// <summary>Frames a session/update notification given the update payload JSON.</summary>
    public void EmitSessionUpdate(string sessionId, string updateJson)
        => Emit($"{{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{{\"sessionId\":\"{sessionId}\",\"update\":{updateJson}}}}}");

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
            _reader.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _serverToClient.Dispose();
        _serverFromClient.Dispose();
    }
}