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
/// <c>DeleteAsync</c>. The cross-table display lookups for user and team
/// names also use <see cref="IDbContextFactory{TContext}"/>; they are used
/// by the audit log UI to resolve actor/subject display data without
/// pulling controllers into the DbContext.
/// </remarks>
public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public AuditLogRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Writes — append-only
    // ==========================================================================

    public async Task AddAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.AuditLogEntries.Add(entry);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ResourceId == resourceId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Include(e => e.Resource)
            .Where(e => e.ResourceId != null && e.RelatedEntityId == userId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        AuditAction? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.AuditLogEntries.AsNoTracking().AsQueryable();

        if (actionFilter.HasValue)
        {
            var action = actionFilter.Value;
            query = query.Where(e => e.Action == action);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e =>
                (e.EntityType == "User" && e.EntityId == userId) ||
                (e.RelatedEntityId == userId))
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<AuditAction>? actions,
        int limit,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(e => e.EntityType == entityType);

        if (entityId.HasValue)
            query = query.Where(e => e.EntityId == entityId.Value);

        if (userId.HasValue)
            query = query.Where(e =>
                e.ActorUserId == userId.Value ||
                e.RelatedEntityId == userId.Value ||
                (e.EntityType == "User" && e.EntityId == userId.Value));

        if (actions is { Count: > 0 })
            query = query.Where(e => actions.Contains(e.Action));

        return await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAllForUserContributorAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(a => a.EntityId == userId || a.RelatedEntityId == userId || a.ActorUserId == userId)
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Cross-table display lookups
    // ==========================================================================

    public async Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(
        IReadOnlyList<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, (string Name, string Slug)>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Teams
            .AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => (t.Name, t.Slug), ct);
    }

    public async Task<IReadOnlyList<Guid>> GetEntityIdsForActionInWindowAsync(
        NodaTime.Instant windowStart,
        NodaTime.Instant windowEnd,
        AuditAction action,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.Action == action
                && e.OccurredAt >= windowStart
                && e.OccurredAt < windowEnd)
            .Select(e => e.EntityId)
            .Distinct()
            .ToListAsync(ct);
    }
}
