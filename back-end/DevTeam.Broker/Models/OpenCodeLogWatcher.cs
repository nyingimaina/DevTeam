using System.Text;

namespace DevTeam.Broker.Models;

/// <summary>
/// Notices when the agent's model call fails, by tailing the agent's own log.
///
/// This exists because opencode reports provider failures (rate limits, unavailable endpoints)
/// **only** there — not over ACP. Its prompt just never returns, so without reading this we can
/// only ever conclude "the agent stopped responding", which hides the actual cause and wastes the
/// whole turn budget.
/// </summary>
public interface IProviderFailureWatcher
{
    /// <summary>
    /// Watches for a stream error against <paramref name="acpSessionId"/> until the returned scope
    /// is disposed. <paramref name="onFailure"/> is invoked at most once.
    /// </summary>
    IDisposable Watch(string acpSessionId, Action<ProviderFailure> onFailure);
}

public sealed class OpenCodeLogWatcher : IProviderFailureWatcher, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string _logPath;
    private readonly TimeSpan _pollInterval;
    private readonly List<CancellationTokenSource> _watches = [];

    public OpenCodeLogWatcher(string logPath, TimeSpan? pollInterval = null)
    {
        _logPath = logPath;
        _pollInterval = pollInterval ?? PollInterval;
    }

    /// <summary>The default location opencode writes its log to on this machine.</summary>
    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "log", "opencode.log");

    public IDisposable Watch(string acpSessionId, Action<ProviderFailure> onFailure)
    {
        var cts = new CancellationTokenSource();
        lock (_watches) _watches.Add(cts);
        // The baseline must be read here, synchronously. Reading it inside the background task let
        // the task start late, so a failure logged moments after Watch() was mistaken for one from
        // an earlier run and silently discarded.
        var offset = CaptureOffset();
        _ = Task.Run(() => WatchAsync(acpSessionId, onFailure, offset, cts.Token));
        return new Scope(this, cts);
    }

    private long CaptureOffset()
    {
        try
        {
            // Only care about what happens from now on; a failure from an earlier run must not
            // abort this one.
            return File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private async Task WatchAsync(string acpSessionId, Action<ProviderFailure> onFailure, long offset, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var failure = ReadNewFailure(acpSessionId, ref offset);
            if (failure is null)
                continue;

            if (failure.IsFailure)
                onFailure(failure);
            return;
        }
    }

    private ProviderFailure? ReadNewFailure(string acpSessionId, ref long offset)
    {
        try
        {
            if (!File.Exists(_logPath))
                return null;

            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < offset)
                offset = 0; // the log rolled over
            if (stream.Length == offset)
                return null;

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var chunk = reader.ReadToEnd();
            offset = stream.Position;

            foreach (var line in chunk.Split('\n'))
            {
                if (!line.Contains("stream error", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!line.Contains($"session.id={acpSessionId}", StringComparison.Ordinal))
                    continue;

                var failure = ProviderFailureClassifier.Classify(line);
                if (failure.IsFailure)
                    return failure;
            }

            return null;
        }
        catch (IOException)
        {
            // The agent may be mid-write; try again on the next tick.
            return null;
        }
    }

    public void Dispose()
    {
        lock (_watches)
        {
            foreach (var cts in _watches)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                cts.Dispose();
            }
            _watches.Clear();
        }
    }

    private sealed class Scope(OpenCodeLogWatcher owner, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            lock (owner._watches) owner._watches.Remove(cts);
            cts.Dispose();
        }
    }
}
