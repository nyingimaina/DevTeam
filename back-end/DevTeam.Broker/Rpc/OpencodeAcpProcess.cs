using System.Diagnostics;
using System.Text;
using DevTeam.Shared;

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

    /// <summary>
    /// Starts the opencode CLI, transparently handling a <c>.cmd</c>/<c>.bat</c> install. npm,
    /// scoop and choco all install a shim rather than an executable, and
    /// <see cref="Process"/> with <c>UseShellExecute=false</c> cannot start one by name, so the
    /// shim is run through <c>cmd.exe /d /s /c</c> (the same approach RepomixRunner uses for npx).
    /// </summary>
    public static OpencodeAcpProcess Create(string executable, IReadOnlyList<string> args, Action<string>? onStderr = null) =>
        OpenCodePathResolver.RequiresShell(executable)
            ? new OpencodeAcpProcess("cmd.exe", BuildShellCommand(executable, args), onStderr)
            : new OpencodeAcpProcess(executable, args, onStderr);

    /// <summary>
    /// Builds the <c>cmd.exe</c> command line that runs a shim and passes its arguments. The whole
    /// command is wrapped in one extra pair of quotes because <c>/s</c> strips exactly the outer
    /// pair, leaving the (possibly space-containing) script path correctly quoted.
    /// </summary>
    public static string BuildShellCommand(string executable, IReadOnlyList<string> args)
    {
        var line = new StringBuilder("/d /s /c \"");
        line.Append(Quote(executable));
        foreach (var arg in args)
            line.Append(' ').Append(Quote(arg));
        line.Append('"');
        return line.ToString();
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;

    public OpencodeAcpProcess(string executable, IReadOnlyList<string> args, Action<string>? onStderr = null)
        : this(WithArguments(NewStartInfo(executable), args), onStderr)
    {
    }

    public OpencodeAcpProcess(string executable, string rawArguments, Action<string>? onStderr = null)
        : this(WithRawArguments(NewStartInfo(executable), rawArguments), onStderr)
    {
    }

    private OpencodeAcpProcess(ProcessStartInfo startInfo, Action<string>? onStderr)
    {
        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'");

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

    private static ProcessStartInfo NewStartInfo(string executable) => new()
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

    private static ProcessStartInfo WithArguments(ProcessStartInfo startInfo, IReadOnlyList<string> args)
    {
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    private static ProcessStartInfo WithRawArguments(ProcessStartInfo startInfo, string rawArguments)
    {
        startInfo.Arguments = rawArguments;
        return startInfo;
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