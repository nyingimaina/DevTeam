using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Context;

/// <summary>
/// Builds and serves the workspace code overview. The refresh is best-effort and never throws to
/// its caller; it builds into a temp folder and only swaps it in once everything succeeded, so a
/// crash or a hung Repomix leaves the previous good overview untouched (spec REQ-005/REQ-007).
/// </summary>
public sealed class RepoContextService : IRepoContextService
{
    private readonly RepoContextOptions _options;
    private readonly RepoContextQueue _queue;
    private readonly IRepomixRunner _repomix;
    private readonly IGitProbe _gitProbe;
    private readonly ILogger<RepoContextService>? _logger;

    // One lock per workspace so two features finishing close together serialise, while two
    // different workspaces refresh in parallel (REQ-005).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public RepoContextService(
        RepoContextOptions options,
        RepoContextQueue queue,
        IRepomixRunner repomix,
        IGitProbe gitProbe,
        ILogger<RepoContextService>? logger = null)
    {
        _options = options;
        _queue = queue;
        _repomix = repomix;
        _gitProbe = gitProbe;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public void EnqueueRefresh(string workspacePath, string trigger)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(workspacePath))
            return;

        _queue.Enqueue(workspacePath, trigger);
    }

    public async Task<RepoContextStatus> GetStatusAsync(string workspacePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return new RepoContextStatus(RepoContextState.None, null, null, []);

        var key = RepoContextQueue.Normalize(workspacePath);
        var index = RepoContextStore.ReadCurrent(workspacePath);
        var meta = index is null ? null : RepoContextStore.ReadMeta(workspacePath, index.Dir);
        var warnings = meta?.Warnings ?? [];

        if (!_options.Enabled)
            return new RepoContextStatus(RepoContextState.Unavailable, null, meta?.BuiltAtUtc, warnings);

        if (_inFlight.ContainsKey(key) || _queue.IsPending(workspacePath))
            return new RepoContextStatus(RepoContextState.Refreshing, null, meta?.BuiltAtUtc, warnings);

        if (meta is null || index is null)
            return new RepoContextStatus(RepoContextState.None, null, null, warnings);

        if (warnings.Any(w => w.StartsWith("repomix-missing", StringComparison.OrdinalIgnoreCase)))
            return new RepoContextStatus(RepoContextState.Unavailable, null, meta.BuiltAtUtc, warnings);

        var probe = await _gitProbe.DescribeAsync(workspacePath, ct);
        if (!probe.IsRepo)
            return new RepoContextStatus(RepoContextState.Unavailable, null, meta.BuiltAtUtc, warnings);

        if (string.Equals(probe.Head, meta.Commit, StringComparison.OrdinalIgnoreCase) && !meta.Dirty)
            return new RepoContextStatus(RepoContextState.UpToDate, 0, meta.BuiltAtUtc, warnings);

        var behind = probe.Head is null ? null : await _gitProbe.CountCommitsAsync(workspacePath, meta.Commit, ct);
        return new RepoContextStatus(RepoContextState.Behind, behind, meta.BuiltAtUtc, warnings);
    }

    public async Task<RepoContextRefreshResult> RefreshAsync(string workspacePath, string trigger, CancellationToken ct)
    {
        if (!_options.Enabled)
            return RepoContextRefreshResult.Skipped("disabled");

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return RepoContextRefreshResult.Skipped("no-workspace");

        var key = RepoContextQueue.Normalize(workspacePath);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        var startedAt = DateTimeOffset.UtcNow;
        _inFlight[key] = 1;
        _logger?.LogInformation(
            "Code overview refreshing: workspace={Workspace} trigger={Trigger}", workspacePath, trigger);

        try
        {
            return await RefreshCoreAsync(workspacePath, trigger, startedAt, ct);
        }
        catch (OperationCanceledException)
        {
            // Broker shutting down mid-refresh: stop cleanly, leave the old index alone.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Code overview refresh failed for {Workspace}; keeping the previous overview", workspacePath);
            return new RepoContextRefreshResult("failed", null, ["refresh-failed"]);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
            gate.Release();
        }
    }

    private async Task<RepoContextRefreshResult> RefreshCoreAsync(
        string workspacePath, string trigger, DateTimeOffset startedAt, CancellationToken ct)
    {
        var before = await _gitProbe.DescribeAsync(workspacePath, ct);
        if (!before.IsRepo || string.IsNullOrWhiteSpace(before.Head))
            return RepoContextRefreshResult.Skipped("not-a-repo");

        var commit = before.Head;
        var dir = RepoContextStore.CommitDirName(commit);

        // Idempotence (§12.20): same commit, already built and clean → just point at it.
        if (RepoContextStore.ReadMeta(workspacePath, dir) is { } existing && !existing.Dirty)
        {
            RepoContextStore.WriteCurrent(workspacePath, new RepoContextIndex(commit, dir, existing.BuiltAtUtc));
            RepoContextStore.EnsureGitExcluded(workspacePath);
            return new RepoContextRefreshResult("up-to-date", commit, existing.Warnings);
        }

        var tempDir = Path.Combine(RepoContextStore.ContextRoot(workspacePath), ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var ignore = _options.BuildIgnoreArgument().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var warnings = new List<string>();

            var full = await _repomix.RunAsync(workspacePath, Path.Combine(tempDir, "pack.xml"), compress: false, ignore, _options.Timeout, ct);
            if (!full.Success)
            {
                warnings.Add(WarningFor(full));
                _logger?.LogWarning("Code overview skipped for {Workspace}: {Outcome} ({Stderr})", workspacePath, full.Outcome, full.StderrPreview);
                return new RepoContextRefreshResult("skipped", null, warnings);
            }

            if (full.Bytes > _options.MaxPackBytes)
            {
                return new RepoContextRefreshResult("skipped", null, ["pack-too-large"]);
            }

            var compressed = await _repomix.RunAsync(workspacePath, Path.Combine(tempDir, "pack.compressed.xml"), compress: true, ignore, _options.Timeout, ct);
            if (!compressed.Success)
            {
                warnings.Add(WarningFor(compressed));
                return new RepoContextRefreshResult("skipped", null, warnings);
            }

            if (compressed.Bytes > _options.MaxPackBytes)
                return new RepoContextRefreshResult("skipped", null, ["pack-too-large"]);

            // If the branch changed while Repomix ran, the pack is from the wrong tree: discard
            // and let a later refresh redo it (§12.9).
            var after = await _gitProbe.DescribeAsync(workspacePath, ct);
            if (!string.Equals(after.Head, commit, StringComparison.OrdinalIgnoreCase))
                return new RepoContextRefreshResult("skipped", null, ["head-changed"]);

            var structure = CodeStructureExtractor.Build(workspacePath);
            File.WriteAllText(Path.Combine(tempDir, "structure.md"), structure);

            var existingMap = ReadExistingMap(workspacePath);
            var map = CodeMapWriter.Build(commit, startedAt, PackReader.ReadFilePaths(compressed.OutputPath), structure, existingMap);
            warnings.AddRange(map.Warnings);
            File.WriteAllText(Path.Combine(tempDir, "CODEBASE_MAP.md"), map.Markdown);

            var meta = new RepoContextMeta
            {
                WorkspacePath = workspacePath,
                Commit = commit,
                Branch = after.Branch ?? before.Branch ?? string.Empty,
                Dirty = after.Dirty,
                BuiltAtUtc = startedAt,
                Trigger = trigger,
                Files = new Dictionary<string, long>
                {
                    ["pack.xml"] = full.Bytes,
                    ["pack.compressed.xml"] = compressed.Bytes,
                    ["structure.md"] = structure.Length,
                    ["CODEBASE_MAP.md"] = map.Markdown.Length,
                },
                ApproxMapTokens = map.ApproxTokens,
                Warnings = warnings,
            };
            RepoContextStore.WriteMeta(tempDir, meta);

            // Atomic swap: the old good index stays in place until every file above is written.
            var destination = RepoContextStore.CommitDir(workspacePath, dir);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(tempDir, destination);

            RepoContextStore.WriteCurrent(workspacePath, new RepoContextIndex(commit, dir, startedAt));
            RepoContextStore.EnsureGitExcluded(workspacePath);
            var pruned = RepoContextStore.Prune(workspacePath, _options.KeepLast, dir);

            _logger?.LogInformation(
                "Code overview refreshed: workspace={Workspace} commit={Commit} trigger={Trigger} durationMs={DurationMs} packBytes={PackBytes} mapTokens={MapTokens} outcome={Outcome} warnings={Warnings} pruned={Pruned}",
                workspacePath, commit, trigger, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
                full.Bytes, map.ApproxTokens, "refreshed", string.Join('|', warnings), pruned.Count);

            return new RepoContextRefreshResult("refreshed", commit, warnings);
        }
        finally
        {
            TryDeleteTemp(tempDir);
        }
    }

    private string? ReadExistingMap(string workspacePath)
    {
        var index = RepoContextStore.ReadCurrent(workspacePath);
        if (index is null)
            return null;

        var path = RepoContextStore.MapPath(workspacePath, index.Dir);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string WarningFor(RepomixResult result) => result.Outcome switch
    {
        RepomixOutcome.Missing => "repomix-missing",
        RepomixOutcome.TimedOut => "repomix-timeout",
        _ => "repomix-failed",
    };

    private void TryDeleteTemp(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
