using Humans.AuditLog.Data;
using System.Transactions;
using Humans.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using Humans.Audit.Contracts;

namespace Humans.Audit.Data;

internal sealed class AuditLogRepository(IDbContextFactory<AuditLogDbContext> factory) : IAuditLogRepository
{
    public async Task AddAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        // A fresh context still enlists in ambient transactions; audit owns its commit.
        using var scope = new TransactionScope(TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.AuditLogEntries.Add(entry);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, IReadOnlyList<string> alertActions, int page, int pageSize, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var query = ctx.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(actionFilter))
            query = query.Where(e => e.Action == actionFilter);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            // arch:db-sort-ok admin page window over append-only audit log
            .OrderByDescending(e => e.OccurredAt)
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
            .Take(pageSize)
            .ToListAsync(ct);

        var alertCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var alertAction in alertActions)
            alertCounts[alertAction] = await ctx.AuditLogEntries.AsNoTracking().CountAsync(e => e.Action == alertAction, ct);

        return (items, totalCount, alertCounts);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, int count, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e =>
                (e.SubjectType == AuditSubjectTypes.User && userIds.Contains(e.SubjectId)) ||
                (e.TargetId.HasValue && userIds.Contains(e.TargetId.Value)))
            // arch:db-sort-ok top-N audit selector
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? subjectType,
        Guid? subjectId,
        IReadOnlyCollection<Guid>? userIds,
        IReadOnlyList<string>? actions,
        int limit,
        bool includeTargets = false,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var query = ctx.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(subjectType))
            query = query.Where(e => e.SubjectType == subjectType);

        if (subjectId.HasValue)
            query = includeTargets
                ? query.Where(e => e.SubjectId == subjectId.Value || e.TargetId == subjectId.Value)
                : query.Where(e => e.SubjectId == subjectId.Value);

        if (userIds is { Count: > 0 })
            query = query.Where(e =>
                (e.ActorUserId.HasValue && userIds.Contains(e.ActorUserId.Value)) ||
                (e.TargetId.HasValue && userIds.Contains(e.TargetId.Value)) ||
                (e.SubjectType == AuditSubjectTypes.User && userIds.Contains(e.SubjectId)));

        if (actions is { Count: > 0 })
            query = query.Where(e => actions.Contains(e.Action));

        return await query
            // arch:db-sort-ok filtered audit window over append-only log
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAllForUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(a =>
                userIds.Contains(a.SubjectId) ||
                (a.TargetId.HasValue && userIds.Contains(a.TargetId.Value)) ||
                (a.ActorUserId.HasValue && userIds.Contains(a.ActorUserId.Value)))
            // arch:db-sort-ok GDPR export stable chronology
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetLegacyGoogleSyncEntriesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ResourceId != null)
            // arch:db-sort-ok one-time migration reads the whole set in order
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);
    }
}
