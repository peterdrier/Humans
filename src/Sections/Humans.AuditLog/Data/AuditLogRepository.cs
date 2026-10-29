using Humans.AuditLog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.AuditLog.Data;

internal sealed class AuditLogRepository(IDbContextFactory<AuditLogDbContext> factory) : IAuditLogRepository
{
    public async Task AddAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
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
                (e.EntityType == "User" && userIds.Contains(e.EntityId)) ||
                (e.RelatedEntityId.HasValue && userIds.Contains(e.RelatedEntityId.Value)))
            // arch:db-sort-ok top-N audit selector
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        IReadOnlyCollection<Guid>? userIds,
        IReadOnlyList<string>? actions,
        int limit,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var query = ctx.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(e => e.EntityType == entityType);

        if (entityId.HasValue)
            query = query.Where(e => e.EntityId == entityId.Value);

        if (userIds is { Count: > 0 })
            query = query.Where(e =>
                (e.ActorUserId.HasValue && userIds.Contains(e.ActorUserId.Value)) ||
                (e.RelatedEntityId.HasValue && userIds.Contains(e.RelatedEntityId.Value)) ||
                (e.EntityType == "User" && userIds.Contains(e.EntityId)));

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
                userIds.Contains(a.EntityId) ||
                (a.RelatedEntityId.HasValue && userIds.Contains(a.RelatedEntityId.Value)) ||
                (a.ActorUserId.HasValue && userIds.Contains(a.ActorUserId.Value)))
            // arch:db-sort-ok GDPR export stable chronology
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<string> actions,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.EntityType == entityType && actions.Contains(e.Action))
            .Select(e => e.EntityId)
            .Distinct()
            .ToListAsync(ct);
        return new HashSet<Guid>(ids);
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
