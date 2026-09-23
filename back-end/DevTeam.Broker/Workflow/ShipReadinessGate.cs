using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Git;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Workflow;

/// <summary>Raised when shipping is blocked because the strict checks didn't pass.</summary>
public sealed class ShipReadinessException : InvalidOperationException
{
    public ShipReadinessException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The branches a merge may not touch without a passing, sha-pinned attestation. "main" is what
/// this app's finalization actually merges into; "master" is honoured as its common alias.
/// </summary>
public static class ProtectedBranches
{
    private static readonly HashSet<string> Names =
        new(StringComparer.OrdinalIgnoreCase) { "main", "master" };

    public static bool Contains(string? branch)
        => !string.IsNullOrWhiteSpace(branch) && Names.Contains(branch);

    public static string Display => "main";
}

/// <summary>One check inside a stored report, for the Checks library.</summary>
public sealed record ReadinessCheckView(
    string PhaseId,
    string Title,
    string Status,
    string Reason,
    long DurationMs,
    ReadinessMetrics Metrics,
    string? RawOutput);

/// <summary>A stored readiness run, shaped for the API/UI.</summary>
public sealed record ReadinessReportView(
    Guid Id,
    Guid? ReleaseId,
    Guid? FeatureId,
    string Scope,
    bool Passed,
    DateTimeOffset StartedAt,
    long DurationMs,
    string? ReleaseVersion,
    IReadOnlyList<ReadinessCheckView> Checks)
{
    public int FailedCount => Checks.Count(c => c.Status == "Failed");
    public int SkippedCount => Checks.Count(c => c.Status == "Skipped");

    /// <summary>Plain-language reason a ship was blocked, safe to show a person directly.</summary>
    public string BlockerSummary => FailedCount == 0
        ? "Every check passed."
        : "The final checks didn't all pass, so this wasn't shipped:\n" +
          string.Join("\n", Checks.Where(c => c.Status == "Failed").Select(c => $"- {c.Title}: {c.Reason}"));
}

/// <summary>
/// The release-level half of the ship gate: runs the strict checks for a whole release, stores
/// the report, and writes the sha-pinned attestation that authorises a merge into a protected
/// branch. Kept separate from the per-feature 'verification' stage (which runs the same checks
/// feature-scoped) so the two gates stay independently testable.
/// </summary>
public interface IShipReadinessGate
{
    Task<ReadinessReportView> CheckReleaseAsync(Guid releaseId, CancellationToken ct);

    Task<ReadinessReportView?> GetLatestAsync(Guid releaseId, CancellationToken ct);

    /// <summary>Every stored run for a release, oldest first — the source for trend lines.</summary>
    Task<IReadOnlyList<ReadinessReportView>> GetHistoryAsync(Guid releaseId, CancellationToken ct);

    Task<bool> HasValidAttestationAsync(string workspacePath, string sourceBranch, CancellationToken ct);
}

public sealed class ShipReadinessGate : IShipReadinessGate
{
    private const int HistoryLimit = 50;

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly IReadinessChecker _checker;
    private readonly IGitService _gitService;

    public ShipReadinessGate(
        IDbContextFactory<DevTeamDbContext> dbFactory, IReadinessChecker checker, IGitService gitService)
    {
        _dbFactory = dbFactory;
        _checker = checker;
        _gitService = gitService;
    }

    public async Task<ReadinessReportView> CheckReleaseAsync(Guid releaseId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var release = await db.Releases.SingleOrDefaultAsync(r => r.Id == releaseId, ct)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        var report = await _checker.CheckAsync(release.WorkspacePath, ReadinessScope.Release, null, ct);
        var commitSha = await ReadCommitShaAsync(release.WorkspacePath, release.BranchName, ct);

        var row = ToRow(report, release.Id, featureId: null, release.Version);
        db.ReadinessReports.Add(row);
        // Record the attestation either way: a failing one is still the latest verdict, so the
        // merge guard sees "checked, and it failed" rather than "never checked".
        db.ReadinessAttestations.Add(new ReadinessAttestation
        {
            ReleaseId = release.Id,
            WorkspacePath = release.WorkspacePath,
            SourceBranch = release.BranchName,
            CommitSha = commitSha,
            ReportId = row.Id,
            Passed = report.Passed,
        });
        await db.SaveChangesAsync(ct);

        return ToView(row);
    }

    public async Task<ReadinessReportView?> GetLatestAsync(Guid releaseId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Ordered client-side: SQLite can't sort a DateTimeOffset column (same pattern as
        // ListHotfixesAsync / GetLatestAssistantTextAsync).
        var rows = await db.ReadinessReports
            .Include(r => r.Checks)
            .Where(r => r.ReleaseId == releaseId)
            .ToListAsync(ct);
        var row = rows.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        return row is null ? null : ToView(row);
    }

    public async Task<IReadOnlyList<ReadinessReportView>> GetHistoryAsync(Guid releaseId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ReadinessReports
            .Include(r => r.Checks)
            .Where(r => r.ReleaseId == releaseId)
            .ToListAsync(ct);
        // Ordered client-side (SQLite can't sort DateTimeOffset) and capped so a long-lived
        // release doesn't grow the chart's payload without bound.
        return rows
            .OrderBy(r => r.CreatedAt)
            .TakeLast(HistoryLimit)
            .Select(ToView)
            .ToList();
    }

    public async Task<bool> HasValidAttestationAsync(string workspacePath, string sourceBranch, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.ReadinessAttestations
            .Where(a => a.WorkspacePath == workspacePath && a.SourceBranch == sourceBranch)
            .ToListAsync(ct);
        var latest = candidates.OrderByDescending(a => a.CreatedAt).FirstOrDefault();

        if (latest is null || !latest.Passed)
            return false;

        // Sha-pinned: new work on the branch invalidates the check and forces a re-run.
        var head = await _gitService.HeadCommitAsync(workspacePath, sourceBranch, ct);
        return head.Success
            && !string.IsNullOrWhiteSpace(head.CommitSha)
            && string.Equals(head.CommitSha, latest.CommitSha, StringComparison.Ordinal);
    }

    private async Task<string> ReadCommitShaAsync(string workspacePath, string branch, CancellationToken ct)
    {
        var head = await _gitService.HeadCommitAsync(workspacePath, branch, ct);
        return head.Success ? head.CommitSha ?? string.Empty : string.Empty;
    }

    private static ReadinessReportRow ToRow(ReadinessReport report, Guid? releaseId, Guid? featureId, string? version)
    {
        var row = new ReadinessReportRow
        {
            WorkspacePath = report.WorkspacePath,
            ReleaseId = releaseId,
            FeatureId = featureId,
            Scope = report.Scope == ReadinessScope.Feature ? "feature" : "release",
            Passed = report.Passed,
            StartedAt = report.StartedAt,
            DurationMs = report.DurationMs,
            ReleaseVersion = version,
        };

        foreach (var phase in report.Phases)
        {
            row.Checks.Add(new ReadinessCheckRow
            {
                PhaseId = phase.Id,
                Title = phase.Title,
                Status = phase.Status.ToString(),
                Reason = phase.Reason,
                DurationMs = phase.DurationMs,
                MetricsJson = JsonSerializer.Serialize(phase.Metrics),
                RawOutput = phase.RawOutput,
            });
        }

        return row;
    }

    private static ReadinessReportView ToView(ReadinessReportRow row) => new(
        row.Id,
        row.ReleaseId,
        row.FeatureId,
        row.Scope,
        row.Passed,
        row.StartedAt,
        row.DurationMs,
        row.ReleaseVersion,
        row.Checks
            .Select(c => new ReadinessCheckView(
                c.PhaseId,
                c.Title,
                c.Status,
                c.Reason,
                c.DurationMs,
                DeserializeMetrics(c.MetricsJson),
                c.RawOutput))
            .ToList());

    private static ReadinessMetrics DeserializeMetrics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ReadinessMetrics.Empty;

        try
        {
            return JsonSerializer.Deserialize<ReadinessMetrics>(json) ?? ReadinessMetrics.Empty;
        }
        catch (JsonException)
        {
            return ReadinessMetrics.Empty;
        }
    }
}
