using DevTeam.Broker.Domain;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Models;

/// <summary>The ordered list of models a workspace will try, and the cooldowns learned along the way.</summary>
public interface IModelCandidateService
{
    Task<IReadOnlyList<ModelCandidate>> ListAsync(string workspacePath, CancellationToken ct);

    /// <summary>Seeds the curated default list on first use, then returns the stored list.</summary>
    Task<IReadOnlyList<ModelCandidate>> ListSeededAsync(string workspacePath, CancellationToken ct);

    Task<ModelCandidate> AddAsync(string workspacePath, string modelId, CancellationToken ct);

    Task RemoveAsync(Guid id, CancellationToken ct);

    Task ReorderAsync(string workspacePath, IReadOnlyList<Guid> orderedIds, CancellationToken ct);

    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct);

    Task RecordFailureAsync(Guid id, ProviderFailure failure, CancellationToken ct);

    Task ClearFailureAsync(Guid id, CancellationToken ct);

    /// <summary>The first enabled candidate that isn't cooling down, in priority order.</summary>
    Task<ModelCandidate?> ResolveActiveAsync(string workspacePath, CancellationToken ct);
}

public sealed class ModelCandidateService : IModelCandidateService
{
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;

    public ModelCandidateService(IDbContextFactory<DevTeamDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<ModelCandidate>> ListAsync(string workspacePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ModelCandidates
            .Where(c => c.WorkspacePath == workspacePath)
            .ToListAsync(ct);

        // Ordered client-side: SQLite can't sort DateTimeOffset, and the list is tiny.
        return rows.OrderBy(c => c.Priority).ToList();
    }

    public async Task<IReadOnlyList<ModelCandidate>> ListSeededAsync(string workspacePath, CancellationToken ct)
    {
        var existing = await ListAsync(workspacePath, ct);
        if (existing.Count > 0)
            return existing;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        for (var i = 0; i < ModelCatalog.SeedModelIds.Count; i++)
        {
            db.ModelCandidates.Add(new ModelCandidate
            {
                WorkspacePath = workspacePath,
                ModelId = ModelCatalog.SeedModelIds[i],
                Priority = i,
                Enabled = true,
                UserAdded = false,
            });
        }
        await db.SaveChangesAsync(ct);

        return await ListAsync(workspacePath, ct);
    }

    public async Task<ModelCandidate> AddAsync(string workspacePath, string modelId, CancellationToken ct)
    {
        // Seed first: otherwise the very first thing a user does — add a model — would create a
        // list containing only that model, silently losing the curated defaults.
        var existing = await ListSeededAsync(workspacePath, ct);

        if (existing.Any(c => string.Equals(c.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{modelId} is already in the list.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidate = new ModelCandidate
        {
            WorkspacePath = workspacePath,
            ModelId = modelId,
            Priority = existing.Count == 0 ? 0 : existing.Max(c => c.Priority) + 1,
            Enabled = true,
            UserAdded = true,
        };
        db.ModelCandidates.Add(candidate);
        await db.SaveChangesAsync(ct);
        return candidate;
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidate = await db.ModelCandidates.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (candidate is null)
            return;

        db.ModelCandidates.Remove(candidate);
        await db.SaveChangesAsync(ct);
        await RenumberAsync(db, candidate.WorkspacePath, ct);
    }

    public async Task ReorderAsync(string workspacePath, IReadOnlyList<Guid> orderedIds, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ModelCandidates
            .Where(c => c.WorkspacePath == workspacePath)
            .ToDictionaryAsync(c => c.Id, ct);

        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (rows.TryGetValue(orderedIds[i], out var row))
                row.Priority = i;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidate = await db.ModelCandidates.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (candidate is null)
            return;

        candidate.Enabled = enabled;
        if (enabled)
            ClearFailureFields(candidate);
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordFailureAsync(Guid id, ProviderFailure failure, CancellationToken ct)
    {
        if (!failure.IsFailure)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidate = await db.ModelCandidates.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (candidate is null)
            return;

        candidate.CooldownUntil = DateTimeOffset.UtcNow + failure.Cooldown;
        candidate.LastFailureKind = failure.Kind.ToString();
        candidate.LastFailureReason = failure.PlainReason;
        candidate.LastFailureAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearFailureAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidate = await db.ModelCandidates.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (candidate is null)
            return;

        ClearFailureFields(candidate);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ModelCandidate?> ResolveActiveAsync(string workspacePath, CancellationToken ct)
    {
        var candidates = await ListSeededAsync(workspacePath, ct);
        var now = DateTimeOffset.UtcNow;
        return candidates.FirstOrDefault(c => c.Enabled && (c.CooldownUntil is null || c.CooldownUntil <= now));
    }

    private static void ClearFailureFields(ModelCandidate candidate)
    {
        candidate.CooldownUntil = null;
        candidate.LastFailureKind = null;
        candidate.LastFailureReason = null;
        candidate.LastFailureAt = null;
    }

    private static async Task RenumberAsync(DevTeamDbContext db, string workspacePath, CancellationToken ct)
    {
        var rows = await db.ModelCandidates
            .Where(c => c.WorkspacePath == workspacePath)
            .ToListAsync(ct);

        var ordered = rows.OrderBy(c => c.Priority).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Priority = i;

        await db.SaveChangesAsync(ct);
    }
}
