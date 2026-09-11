using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Workgroups.Data;

internal sealed class WorkgroupRepository(IDbContextFactory<WorkgroupsDbContext> factory) : IWorkgroupRepository
{
    // ── Register graph ────────────────────────────────────────────────────

    public async Task<WorkgroupsGraph> GetGraphAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Identity resolution rather than plain AsNoTracking: the child → Workgroup
        // back-references make the graph cyclic, which no-tracking queries refuse.
        var workgroups = await Register(ctx).ToListAsync(ct);
        return new WorkgroupsGraph(workgroups);
    }

    // ── Single rows for mutation ──────────────────────────────────────────

    public async Task<Workgroup?> GetWorkgroupAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await Register(ctx).FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    public async Task<Workgroup?> GetWorkgroupBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await Register(ctx).FirstOrDefaultAsync(w => w.Slug == slug, ct);
    }

    public async Task<bool> SlugTakenAsync(string slug, Guid? exceptId = null, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Workgroups.AsNoTracking()
            .AnyAsync(w => w.Slug == slug && (exceptId == null || w.Id != exceptId), ct);
    }

    public async Task<WorkgroupMember?> GetMemberAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<WorkgroupMeeting?> GetMeetingAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Meetings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<WorkgroupLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.LogEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<WorkgroupDocument?> GetDocumentAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Documents.AsNoTrackingWithIdentityResolution()
            .Include(d => d.Comments)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<WorkgroupDocumentComment?> GetCommentAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Comments.AsNoTrackingWithIdentityResolution()
            .Include(c => c.Document)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    // ── Writes ────────────────────────────────────────────────────────────

    public async Task AddWorkgroupAsync(Workgroup workgroup, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Workgroups.Add(workgroup);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateWorkgroupAsync(Workgroup workgroup, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(workgroup);
        ctx.Entry(workgroup).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddMemberAsync(WorkgroupMember member, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Members.Add(member);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateMembersAsync(IReadOnlyList<WorkgroupMember> members, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        foreach (var member in members)
        {
            ctx.Attach(member);
            ctx.Entry(member).State = EntityState.Modified;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddMeetingAsync(WorkgroupMeeting meeting, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Meetings.Add(meeting);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateMeetingAsync(WorkgroupMeeting meeting, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(meeting);
        ctx.Entry(meeting).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddLogEntryAsync(WorkgroupLogEntry entry, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.LogEntries.Add(entry);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateLogEntryAsync(WorkgroupLogEntry entry, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(entry);
        ctx.Entry(entry).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteLogEntryAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Tracked remove rather than ExecuteDelete so the section's EF-InMemory tests
        // exercise the same path.
        var entry = await ctx.LogEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry == null)
            return;

        ctx.LogEntries.Remove(entry);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddDocumentAsync(WorkgroupDocument document, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Documents.Add(document);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateDocumentAsync(WorkgroupDocument document, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(document);
        ctx.Entry(document).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddCommentAsync(WorkgroupDocumentComment comment, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Comments.Add(comment);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateCommentsAsync(
        IReadOnlyList<WorkgroupDocumentComment> comments, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        foreach (var comment in comments)
        {
            ctx.Attach(comment);
            ctx.Entry(comment).State = EntityState.Modified;
        }
        await ctx.SaveChangesAsync(ct);
    }

    // ── GDPR ──────────────────────────────────────────────────────────────

    public async Task<WorkgroupUserRows> GetRowsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var memberships = await ctx.Members.AsNoTracking()
            .Where(m => m.UserId == userId).ToListAsync(ct);
        var logEntries = await ctx.LogEntries.AsNoTracking()
            .Where(e => e.AuthorUserId == userId).ToListAsync(ct);
        var meetings = await ctx.Meetings.AsNoTracking()
            .Where(m => m.CreatedByUserId == userId).ToListAsync(ct);
        // DispositionByUserId too: a Board member who records a disposition on someone else's
        // document carries attribution on that row, and the erasure nulls that column as well.
        var documents = await ctx.Documents.AsNoTracking()
            .Where(d => d.CreatedByUserId == userId
                || d.UpdatedByUserId == userId
                || d.DispositionByUserId == userId)
            .ToListAsync(ct);
        // Hidden comments are included: the export is what we hold, not what we show. All three
        // attribution columns are matched, so a responder or moderator sees the rows that carry
        // their id even when someone else wrote the comment — the same three the erasure nulls.
        var comments = await ctx.Comments.AsNoTracking()
            .Where(c => c.AuthorUserId == userId
                || c.RespondedByUserId == userId
                || c.HiddenByUserId == userId)
            .ToListAsync(ct);

        return new WorkgroupUserRows(memberships, logEntries, meetings, documents, comments);
    }

    public async Task EraseUserAsync(Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var memberships = await ctx.Members.Where(m => m.UserId == userId).ToListAsync(ct);
        ctx.Members.RemoveRange(memberships);

        foreach (var w in await ctx.Workgroups.Where(w => w.AppliedByUserId == userId).ToListAsync(ct))
        {
            w.AppliedByUserId = null;
            w.UpdatedAt = now;
        }

        foreach (var e in await ctx.LogEntries.Where(e => e.AuthorUserId == userId).ToListAsync(ct))
        {
            e.AuthorUserId = null;
            e.UpdatedAt = now;
        }

        foreach (var m in await ctx.Meetings.Where(m => m.CreatedByUserId == userId).ToListAsync(ct))
        {
            m.CreatedByUserId = null;
            m.UpdatedAt = now;
        }

        foreach (var d in await ctx.Documents
            .Where(d => d.CreatedByUserId == userId
                || d.UpdatedByUserId == userId
                || d.DispositionByUserId == userId)
            .ToListAsync(ct))
        {
            if (d.CreatedByUserId == userId) d.CreatedByUserId = null;
            if (d.UpdatedByUserId == userId) d.UpdatedByUserId = null;
            if (d.DispositionByUserId == userId) d.DispositionByUserId = null;
            d.UpdatedAt = now;
        }

        // Comment bodies, responses and hide reasons stay — the association's record
        // (design §18). Only the attribution goes.
        foreach (var c in await ctx.Comments
            .Where(c => c.AuthorUserId == userId
                || c.RespondedByUserId == userId
                || c.HiddenByUserId == userId)
            .ToListAsync(ct))
        {
            if (c.AuthorUserId == userId) c.AuthorUserId = null;
            if (c.RespondedByUserId == userId) c.RespondedByUserId = null;
            if (c.HiddenByUserId == userId) c.HiddenByUserId = null;
        }

        await ctx.SaveChangesAsync(ct);
    }

    // ── Account merge ─────────────────────────────────────────────────────

    public async Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        foreach (var w in await ctx.Workgroups.Where(w => w.AppliedByUserId == sourceUserId).ToListAsync(ct))
        {
            w.AppliedByUserId = targetUserId;
            w.UpdatedAt = now;
        }

        foreach (var e in await ctx.LogEntries.Where(e => e.AuthorUserId == sourceUserId).ToListAsync(ct))
        {
            e.AuthorUserId = targetUserId;
            e.UpdatedAt = now;
        }

        foreach (var m in await ctx.Meetings.Where(m => m.CreatedByUserId == sourceUserId).ToListAsync(ct))
        {
            m.CreatedByUserId = targetUserId;
            m.UpdatedAt = now;
        }

        foreach (var d in await ctx.Documents
            .Where(d => d.CreatedByUserId == sourceUserId
                || d.UpdatedByUserId == sourceUserId
                || d.DispositionByUserId == sourceUserId)
            .ToListAsync(ct))
        {
            if (d.CreatedByUserId == sourceUserId) d.CreatedByUserId = targetUserId;
            if (d.UpdatedByUserId == sourceUserId) d.UpdatedByUserId = targetUserId;
            if (d.DispositionByUserId == sourceUserId) d.DispositionByUserId = targetUserId;
            d.UpdatedAt = now;
        }

        foreach (var c in await ctx.Comments
            .Where(c => c.AuthorUserId == sourceUserId
                || c.RespondedByUserId == sourceUserId
                || c.HiddenByUserId == sourceUserId)
            .ToListAsync(ct))
        {
            if (c.AuthorUserId == sourceUserId) c.AuthorUserId = targetUserId;
            if (c.RespondedByUserId == sourceUserId) c.RespondedByUserId = targetUserId;
            if (c.HiddenByUserId == sourceUserId) c.HiddenByUserId = targetUserId;
        }

        var affected = await ctx.Members
            .Where(m => m.UserId == sourceUserId || m.UserId == targetUserId)
            .ToListAsync(ct);

        foreach (var m in affected.Where(m => m.UserId == sourceUserId))
            m.UserId = targetUserId;

        // The fold can leave two current memberships of the same group; the unique
        // filtered index would refuse them. Keep the earliest join, and keep Coordinator
        // if either row carried it.
        var duplicates = affected
            .Where(m => m.LeftAt == null)
            .GroupBy(m => m.WorkgroupId)
            .Where(g => g.Count() > 1)
            .SelectMany(g =>
            {
                var keep = g.MinBy(m => m.JoinedAt)!;
                if (g.Any(m => m.Role == WorkgroupMemberRole.Coordinator))
                    keep.Role = WorkgroupMemberRole.Coordinator;
                return g.Where(m => m != keep);
            })
            .ToList();

        ctx.Members.RemoveRange(duplicates);

        await ctx.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// The register read shape, detached with identity resolution so the child →
    /// <c>Workgroup</c> back-references are fixed up without tracking.
    /// </summary>
    private static IQueryable<Workgroup> Register(WorkgroupsDbContext ctx) =>
        ctx.Workgroups.AsNoTrackingWithIdentityResolution()
            .Include(w => w.Members)
            .Include(w => w.Meetings)
            .Include(w => w.LogEntries)
            .Include(w => w.Documents).ThenInclude(d => d.Comments);
}
