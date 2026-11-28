using Humans.AuditLog.Data;
using System.Transactions;
using Humans.Audit.Domain;
using Humans.Audit.Services;
using Microsoft.EntityFrameworkCore;
using Humans.Audit.Contracts;
using NodaTime;
using Humans.Base.Data;

namespace Humans.Audit.Data;

internal sealed class AuditLogRepository(IDbContextFactory<AuditLogDbContext> factory) : SectionRepository<AuditLogDbContext>(factory), IAuditLogRepository
{
    public async Task AddAsync(AuditLogEntrySnapshot entry, CancellationToken ct = default)
    {
        // A fresh context still enlists in ambient transactions; audit owns its commit.
        using var scope = new TransactionScope(TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);
        await using var ctx = await CreateDbContextAsync(ct);
        switch (entry.Kept)
        {
            case AuditKept.Forever:
                ctx.AuditLogEntries.Add(new AuditLogEntry
                {
                    Id = entry.Id,
                    Action = entry.Action,
                    SubjectType = entry.SubjectType,
                    SubjectId = entry.SubjectId,
                    Description = entry.Description,
                    OccurredAt = entry.OccurredAt,
                    ActorUserId = entry.ActorUserId,
                    JobName = entry.JobName,
                    TargetId = entry.TargetId,
                    TargetType = entry.TargetType
                });
                break;
            case AuditKept.RetentionPeriod:
                ctx.TemporaryAuditLogEntries.Add(new TemporaryAuditLogEntry
                {
                    Id = entry.Id,
                    Action = entry.Action,
                    SubjectType = entry.SubjectType,
                    SubjectId = entry.SubjectId,
                    Description = entry.Description,
                    OccurredAt = entry.OccurredAt,
                    ActorUserId = entry.ActorUserId,
                    JobName = entry.JobName,
                    TargetId = entry.TargetId,
                    TargetType = entry.TargetType
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry.Kept, "Unknown audit side.");
        }
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Both sides as one query (UNION ALL), so filters, order and paging run in the database. EF translates a union
    /// only over member-init projections, hence <see cref="Row"/> rather than the snapshot record.
    /// </summary>
    private static IQueryable<Row> Rows(AuditLogDbContext ctx) =>
        ctx.AuditLogEntries.AsNoTracking()
            .Select(e => new Row
            {
                Id = e.Id,
                Action = e.Action,
                SubjectType = e.SubjectType,
                SubjectId = e.SubjectId,
                Description = e.Description,
                OccurredAt = e.OccurredAt,
                ActorUserId = e.ActorUserId,
                JobName = e.JobName,
                TargetId = e.TargetId,
                TargetType = e.TargetType,
                Temporary = false
            })
            .Concat(ctx.TemporaryAuditLogEntries.AsNoTracking()
                .Select(e => new Row
                {
                    Id = e.Id,
                    Action = e.Action,
                    SubjectType = e.SubjectType,
                    SubjectId = e.SubjectId,
                    Description = e.Description,
                    OccurredAt = e.OccurredAt,
                    ActorUserId = e.ActorUserId,
                    JobName = e.JobName,
                    TargetId = e.TargetId,
                    TargetType = e.TargetType,
                    Temporary = true
                }));

    private static async Task<IReadOnlyList<AuditLogEntrySnapshot>> ToSnapshotsAsync(IQueryable<Row> rows, CancellationToken ct) =>
        (await rows.ToListAsync(ct))
            .Select(r => new AuditLogEntrySnapshot(r.Id, r.Action, r.SubjectType, r.SubjectId, r.Description,
                r.OccurredAt, r.ActorUserId, r.TargetId, r.TargetType,
                r.Temporary ? AuditKept.RetentionPeriod : AuditKept.Forever, r.JobName))
            .ToList();

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Action { get; init; } = string.Empty;
        public string SubjectType { get; init; } = string.Empty;
        public Guid SubjectId { get; init; }
        public string Description { get; init; } = string.Empty;
        public Instant OccurredAt { get; init; }
        public Guid? ActorUserId { get; init; }
        public string? JobName { get; init; }
        public Guid? TargetId { get; init; }
        public string? TargetType { get; init; }
        public bool Temporary { get; init; }
    }

    public async Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, IReadOnlyList<string> alertActions, int page, int pageSize, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

        var query = Rows(ctx);

        if (!string.IsNullOrEmpty(actionFilter))
            query = query.Where(e => e.Action == actionFilter);

        var totalCount = await query.CountAsync(ct);

        var items = await ToSnapshotsAsync(query
            // arch:db-sort-ok admin page window over append-only audit log
            .OrderByDescending(e => e.OccurredAt)
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
            .Take(pageSize), ct);

        var alertCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var alertAction in alertActions)
            alertCounts[alertAction] = await Rows(ctx).CountAsync(e => e.Action == alertAction, ct);

        return (items, totalCount, alertCounts);
    }

    public async Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, int count, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await CreateDbContextAsync(ct);
        return await ToSnapshotsAsync(Rows(ctx)
            .Where(e =>
                (e.SubjectType == AuditSubjectTypes.User && userIds.Contains(e.SubjectId)) ||
                (e.TargetId.HasValue && userIds.Contains(e.TargetId.Value)))
            // arch:db-sort-ok top-N audit selector
            .OrderByDescending(e => e.OccurredAt)
            .Take(count), ct);
    }

    public async Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? subjectType,
        Guid? subjectId,
        IReadOnlyCollection<Guid>? userIds,
        IReadOnlyList<string>? actions,
        int limit,
        bool includeTargets = false,
        CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

        var query = Rows(ctx);

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

        return await ToSnapshotsAsync(query
            // arch:db-sort-ok filtered audit window over append-only log
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit), ct);
    }

    public async Task<IReadOnlyList<AuditLogEntrySnapshot>> GetAllForUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await CreateDbContextAsync(ct);
        return await ToSnapshotsAsync(Rows(ctx)
            .Where(a =>
                userIds.Contains(a.SubjectId) ||
                (a.TargetId.HasValue && userIds.Contains(a.TargetId.Value)) ||
                (a.ActorUserId.HasValue && userIds.Contains(a.ActorUserId.Value)))
            // arch:db-sort-ok GDPR export stable chronology
            .OrderByDescending(a => a.OccurredAt), ct);
    }

    public async Task<int> DeleteTemporaryBeforeAsync(Instant cutoff, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var old = await ctx.TemporaryAuditLogEntries.Where(e => e.OccurredAt < cutoff).ToListAsync(ct);
        ctx.TemporaryAuditLogEntries.RemoveRange(old);
        await ctx.SaveChangesAsync(ct);
        return old.Count;
    }

    public async Task<int> DeleteTemporaryForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var rows = await ctx.TemporaryAuditLogEntries
            .Where(e => e.SubjectId == userId || e.TargetId == userId || e.ActorUserId == userId)
            .ToListAsync(ct);
        ctx.TemporaryAuditLogEntries.RemoveRange(rows);
        await ctx.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetLegacyGoogleSyncEntriesAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ResourceId != null)
            // arch:db-sort-ok one-time migration reads the whole set in order
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct), ct);
}
