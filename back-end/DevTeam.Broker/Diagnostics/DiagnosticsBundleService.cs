using System.IO.Compression;
using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Metrics;
using DevTeam.Broker.Models;
using DevTeam.Broker.Server;
using DevTeam.Shared;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Diagnostics;

/// <summary>One error the web UI recorded, included in the bundle so server and client line up.</summary>
public sealed record FrontendErrorDto(string? At, string? Message, string? Url, int? Status, string? RequestId);

public sealed record DiagnosticsBundleRequest(IReadOnlyList<FrontendErrorDto>? FrontendErrors = null);

/// <summary>
/// Builds a single file a novice can hand to a technical support specialist: what the app is,
/// what state it is in, what the agent is doing right now, and the recent logs — all together,
/// so the specialist does not have to talk the user through finding anything.
/// </summary>
public sealed class DiagnosticsBundleService
{
    private const int LogBytesPerFile = 1024 * 1024;
    // The agent's own log is the only place a model-provider refusal is recorded, so it belongs in
    // the bundle even though it is not ours.
    private const int OpenCodeLogBytes = 512 * 1024;
    private const int LogFilesToInclude = 2;
    private const int ReleasesToInclude = 5;
    private const int RunsPerFeature = 5;
    private const int ChecksPerRun = 12;
    private const int EvidenceChars = 2000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly RuntimeIdentity _identity;
    private readonly IAppInfo _appInfo;
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly ActiveTurnTracker _turnTracker;
    private readonly DiagnosticsSettings _settings;
    private readonly IModelCandidateService _modelCandidates;
    private readonly MetricsService _metrics;
    private readonly string _openCodeLogPath;

    public DiagnosticsBundleService(
        RuntimeIdentity identity,
        IAppInfo appInfo,
        IDbContextFactory<DevTeamDbContext> dbFactory,
        ActiveTurnTracker turnTracker,
        DiagnosticsSettings settings,
        IModelCandidateService modelCandidates,
        MetricsService metrics)
    {
        _identity = identity;
        _appInfo = appInfo;
        _dbFactory = dbFactory;
        _turnTracker = turnTracker;
        _settings = settings;
        _modelCandidates = modelCandidates;
        _metrics = metrics;
        _openCodeLogPath = OpenCodeLogWatcher.DefaultLogPath;
    }

    public async Task<byte[]> BuildAsync(DiagnosticsBundleRequest? request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(zip, "app-info.json", AppInfo());
            WriteJson(zip, "settings.json", new { diagnostics = new { verboseLogging = _settings.VerboseLogging } });
            WriteJson(zip, "state.json", await StateAsync(ct));
            WriteJson(zip, "models.json", await ModelsAsync(ct));
            WriteJson(zip, "frontend-errors.json", request?.FrontendErrors ?? []);
            await WriteMetricsAsync(zip, ct);
            WriteRecentLogs(zip);
            WriteOpenCodeLog(zip);
        }

        return buffer.ToArray();
    }

    private object AppInfo() => new
    {
        version = _appInfo.Version,
        port = _identity.Port,
        dataDirectory = _identity.DataDirectory,
        databasePath = _identity.DatabasePath,
        logsDirectory = _identity.LogsDirectory,
        opencodePath = _identity.OpenCodePath,
        opencodeRequiresShell = _identity.OpenCodeRequiresShell,
        opencodeDesktopAppInstalled = _identity.OpenCodeDesktopAppInstalled,
        opencodeSearchedDirectories = _identity.OpenCodeSearchedDirectories,
        opencodeLogPath = _openCodeLogPath,
        os = Environment.OSVersion.ToString(),
        framework = Environment.Version.ToString(),
        machineName = Environment.MachineName,
        collectedAt = DateTimeOffset.UtcNow,
    };

    private async Task<object> StateAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Ordered client-side: SQLite can't sort DateTimeOffset columns.
        var releases = await db.Releases
            .Include(r => r.Features).ThenInclude(f => f.StageRuns).ThenInclude(sr => sr.GateChecks)
            .ToListAsync(ct);

        var releaseSummaries = releases
            .OrderByDescending(r => r.UpdatedAt)
            .Take(ReleasesToInclude)
            .Select(SummarizeRelease)
            .ToList();

        var audits = await db.AuditEvents.ToListAsync(ct);

        return new
        {
            currentTurn = TurnSnapshot(),
            releases = releaseSummaries,
            recentAudit = audits
                .OrderByDescending(a => a.CreatedAt)
                .Take(50)
                .Select(a => new { a.Kind, a.SessionId, a.CreatedAt }),
        };
    }

    private object? TurnSnapshot()
    {
        var turn = _turnTracker.Current;
        if (turn is null)
            return null;

        return new
        {
            turn.SessionId,
            turn.StageRunSessionId,
            turn.AcpSessionId,
            turn.StartedAt,
            turn.LastEventAt,
            turn.QueuedTurns,
            turn.IsPriming,
            activity = (turn.Activity ?? [])
                .TakeLast(20)
                .Select(a => new { a.At, a.Kind, a.Label, a.Status }),
        };
    }

    private static object SummarizeRelease(DevTeamRelease release) => new
    {
        release.Id,
        release.Title,
        release.Version,
        status = release.Status.ToString(),
        release.BranchName,
        release.IsHotfix,
        release.WorkspacePath,
        release.UpdatedAt,
        features = release.Features.Select(f => new
        {
            f.Key,
            f.Title,
            status = f.Status.ToString(),
            currentStage = f.FlowPosition?.CurrentStageName,
            stageIndex = f.FlowPosition?.CurrentStageIndex,
            stageRuns = f.StageRuns
                .OrderByDescending(sr => sr.StartedAt)
                .Take(RunsPerFeature)
                .Select(SummarizeRun),
        }),
    };

    private static object SummarizeRun(ReleaseStageRun run) => new
    {
        run.Id,
        run.StageName,
        status = run.Status.ToString(),
        phase = run.Phase.ToString(),
        run.Attempt,
        run.StartedAt,
        run.FinishedAt,
        run.ReadyToProceed,
        run.Summary,
        lastErrorKind = run.LastErrorKind.ToString(),
        run.LastErrorMessage,
        run.LastErrorAt,
        run.CheckpointJson,
        // The check ledger is where "why did it fail?" actually lives.
        checks = run.GateChecks
            .OrderByDescending(c => c.StartedAt)
            .Take(ChecksPerRun)
            .Select(SummarizeCheck),
    };

    private static object SummarizeCheck(ReleaseGateCheck check) => new
    {
        check.Name,
        check.Passed,
        check.IsEntryGate,
        check.ResponsibleRole,
        check.CompletedAt,
        check.DisplayTitle,
        check.PlainProblem,
        evidence = Truncate(check.EvidenceText, EvidenceChars),
    };

    // What the model failover list looks like right now, plus which model each recent session was
    // asked for vs what it is actually running — the "is my model being used?" question.
    private async Task<object> ModelsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var releases = await db.Releases.ToListAsync(ct);
        var workspacePath = releases.OrderByDescending(r => r.UpdatedAt).FirstOrDefault()?.WorkspacePath;

        var candidates = string.IsNullOrWhiteSpace(workspacePath)
            ? []
            : (await _modelCandidates.ListAsync(workspacePath, ct))
                .Select(c => new
                {
                    c.ModelId,
                    c.Priority,
                    c.Enabled,
                    c.UserAdded,
                    c.CooldownUntil,
                    c.LastFailureKind,
                    c.LastFailureReason,
                })
                .ToList();

        var sessions = (await db.Sessions.ToListAsync(ct))
            .OrderByDescending(s => s.UpdatedAt)
            .Take(10)
            .Select(s => new { s.Id, s.WorkspacePath, requestedModelId = s.RequestedModelId, appliedModelId = s.ModelId, s.ModeId });

        return new { workspacePath, candidates, recentSessions = sessions };
    }

    private void WriteOpenCodeLog(ZipArchive zip)
    {
        try
        {
            if (!File.Exists(_openCodeLogPath))
                return;

            using var source = new FileStream(_openCodeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = Math.Min(OpenCodeLogBytes, source.Length);
            source.Seek(-length, SeekOrigin.End);

            var entry = zip.CreateEntry("logs/opencode.log", CompressionLevel.Fastest);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (IOException)
        {
            // The agent may be mid-write; skip rather than fail the whole bundle.
        }
    }

    private void WriteRecentLogs(ZipArchive zip)
    {
        if (!Directory.Exists(_identity.LogsDirectory))
            return;

        var newest = Directory.EnumerateFiles(_identity.LogsDirectory, "*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(LogFilesToInclude);

        foreach (var file in newest)
        {
            try
            {
                using var source = file.OpenRead();
                var length = Math.Min(LogBytesPerFile, source.Length);
                source.Seek(-length, SeekOrigin.End);

                var entry = zip.CreateEntry($"logs/{file.Name}", CompressionLevel.Fastest);
                using var target = entry.Open();
                source.CopyTo(target);
            }
            catch (IOException)
            {
                // A log file being written to right now may refuse to open; skip it rather than
                // failing the whole bundle.
            }
        }
    }

    // The efficiency diagnosis rides along with the bundle, so a hand-off includes "what it cost
    // and what looks wasteful" without anyone needing database access.
    private async Task WriteMetricsAsync(ZipArchive zip, CancellationToken ct)
    {
        try
        {
            var summary = await _metrics.SummarizeAsync(new MetricsScope(null, null, null, 30), ct);
            WriteJson(zip, "metrics.json", summary);
            WriteText(zip, "metrics.md", MetricsMarkdown.Render(summary));
        }
        catch (Exception)
        {
            // A bundle without metrics is still a useful bundle.
        }
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static void WriteJson(ZipArchive zip, string name, object payload)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(payload, Json));
    }

    private static string? Truncate(string? text, int max)
        => text is null || text.Length <= max ? text : text[..max] + "\n…(truncated)";
}
