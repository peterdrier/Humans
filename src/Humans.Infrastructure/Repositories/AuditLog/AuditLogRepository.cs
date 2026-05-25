using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.AuditLog;

/// <summary>
/// EF-backed implementation of <see cref="IAuditLogRepository"/>. The only
/// non-test file that touches <c>DbContext.AuditLogEntries</c> after the
/// Audit Log migration lands. Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
/// <remarks>
/// <c>audit_log</c> is append-only per design-rules §12 — only
/// <see cref="AddAsync"/> is exposed; there are no <c>UpdateAsync</c> or
/// <c>DeleteAsync</c>.
/// </remarks>
internal sealed class AuditLogRepository(IDbContextFactory<HumansDbContext> factory) : IAuditLogRepository
{
    // ==========================================================================
    // Writes — append-only
    // ==========================================================================

    public async Task AddAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.AuditLogEntries.Add(entry);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ResourceId == resourceId)
            .OrderByDescending(e => e.OccurredAt) // arch:db-sort-ok top-N selector
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ResourceId != null && e.RelatedEntityId == userId)
            // arch:db-sort-ok top-N audit selector
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ResourceId != null
                && e.RelatedEntityId.HasValue
                && userIds.Contains(e.RelatedEntityId.Value))
            // arch:db-sort-ok top-N audit selector
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            // arch:db-sort-ok top-N audit selector
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        AuditAction? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var query = ctx.AuditLogEntries.AsNoTracking().AsQueryable();

        if (actionFilter.HasValue)
        {
            var action = actionFilter.Value;
            query = query.Where(e => e.Action == action);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            // arch:db-sort-ok admin page window over append-only audit log
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var anomalyCount = await ctx.AuditLogEntries
            .AsNoTracking()
            .CountAsync(e => e.Action == AuditAction.AnomalousPermissionDetected, ct);

        return (items, totalCount, anomalyCount);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e =>
                (e.EntityType == "User" && e.EntityId == userId) ||
                (e.RelatedEntityId == userId))
            // arch:db-sort-ok top-N audit selector
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
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
        IReadOnlyList<AuditAction>? actions,
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

    public async Task<IReadOnlyList<AuditLogEntry>> GetAllForUserContributorAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(a => a.EntityId == userId || a.RelatedEntityId == userId || a.ActorUserId == userId)
            // arch:db-sort-ok GDPR export stable chronology
            .OrderByDescending(a => a.OccurredAt)
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

    public async Task<IReadOnlyList<Guid>> GetEntityIdsForActionInWindowAsync(
        NodaTime.Instant windowStart,
        NodaTime.Instant windowEnd,
        AuditAction action,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.Action == action
                && e.OccurredAt >= windowStart
                && e.OccurredAt < windowEnd)
            .Select(e => e.EntityId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<AuditAction> actions,
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
}
