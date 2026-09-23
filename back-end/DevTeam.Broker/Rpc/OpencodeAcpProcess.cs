using System.Diagnostics;
using System.Text;

namespace DevTeam.Broker.Rpc;

/// <summary>
/// Spawns <c>opencode acp</c> and adapts its stdio to the newline-delimited
/// JSON frame contract of <see cref="IAcpProcess"/>.
/// </summary>
public sealed class OpencodeAcpProcess : IAcpProcess
{
    private readonly Process _process;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private int _disposed;

    public OpencodeAcpProcess(string executable, IReadOnlyList<string> args, Action<string>? onStderr = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException($"Failed to start '{executable}'");

        _reader = new StreamReader(_process.StandardOutput.BaseStream, new UTF8Encoding(false));
        _writer = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
        {
            NewLine = "\n",
            AutoFlush = true,
        };

        // ALWAYS drain stderr, even when nobody wants the text: a redirected-but-unread pipe
        // fills up (4-64KB) and the child then blocks writing to it — which looks exactly like
        // the agent going silent forever. This is a correctness fix, not just diagnostics.
        _ = Task.Run(() => DrainStandardErrorAsync(onStderr));
    }

    private async Task DrainStandardErrorAsync(Action<string>? onStderr)
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardError.ReadLineAsync();
                if (line is null)
                    return;

                if (onStderr is not null && !string.IsNullOrWhiteSpace(line))
                    onStderr(line);
            }
        }
        catch (Exception)
        {
            // The process exiting mid-read is normal; nothing to report.
        }
    }

    public int ProcessId => _process.Id;

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => _reader.ReadLineAsync(cancellationToken).AsTask();

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        lock (_writeLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.WriteLine(line);
        }

        return Task.CompletedTask;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Kill();
        _reader.Dispose();
        _writer.Dispose();
        _process.Dispose();
    }
}