using Humans.Feedback.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Feedback.Domain;

namespace Humans.Feedback.Data;

internal sealed class FeedbackRepository(IDbContextFactory<FeedbackDbContext> factory) : IFeedbackRepository
{
    public async Task<FeedbackReport?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.FeedbackReports
            .AsNoTracking()
            .Include(f => f.Messages.OrderBy(m => m.CreatedAt)) // arch:db-sort-ok message thread chronological order
            .FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    public async Task<IReadOnlyList<FeedbackMessage>> GetPendingReplyEmailsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.FeedbackMessages.AsNoTracking().Where(m => m.EmailEnqueuePending == true).ToListAsync(ct);
    }

    public async Task<bool> CompleteReplyEmailAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var message = await ctx.FeedbackMessages.FindAsync([messageId], ct);
        if (message?.EmailEnqueuePending != true) return false;
        message.EmailEnqueuePending = false;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<FeedbackReport?> FindForMutationAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.FeedbackReports
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    public async Task<IReadOnlyList<FeedbackReport>> GetListAsync(
        FeedbackStatus? status,
        FeedbackCategory? category,
        Guid? reporterUserId,
        Guid? assignedToUserId,
        Guid? assignedToTeamId,
        bool? unassignedOnly,
        int limit,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.FeedbackReports
            .AsNoTracking()
            .Include(f => f.Messages)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(f => f.Status == status.Value);

        if (category.HasValue)
            query = query.Where(f => f.Category == category.Value);

        if (reporterUserId.HasValue)
            query = query.Where(f => f.UserId == reporterUserId.Value);

        if (assignedToUserId.HasValue)
            query = query.Where(f => f.AssignedToUserId == assignedToUserId.Value);

        if (assignedToTeamId.HasValue)
            query = query.Where(f => f.AssignedToTeamId == assignedToTeamId.Value);

        if (unassignedOnly == true)
            query = query.Where(f => f.AssignedToUserId == null && f.AssignedToTeamId == null);

        return await query
            .OrderByDescending(f => f.CreatedAt) // arch:db-sort-ok top-N selector
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> GetActionableCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.FeedbackReports
            .Where(f => f.Status != FeedbackStatus.Resolved && f.Status != FeedbackStatus.WontFix)
            .CountAsync(f =>
                (f.Status == FeedbackStatus.Open && f.LastAdminMessageAt == null) ||
                (f.LastReporterMessageAt != null &&
                 (f.LastAdminMessageAt == null || f.LastReporterMessageAt > f.LastAdminMessageAt)),
                ct);
    }

    public async Task<IReadOnlyList<(Guid UserId, int Count)>> GetReporterCountsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.FeedbackReports
            .AsNoTracking()
            .GroupBy(f => f.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.Select(r => (r.UserId, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<FeedbackReport>> GetForUserExportAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.FeedbackReports
            .AsNoTracking()
            .Include(fr => fr.Messages)
            .Where(fr => fr.UserId == userId || fr.Messages.Any(m => m.SenderUserId == userId))
            .ToListAsync(ct);
    }

    public async Task SaveTrackedReportAsync(FeedbackReport report, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(report);
        ctx.Entry(report).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddMessageAndSaveReportAsync(
        FeedbackMessage message, FeedbackReport report, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // The caller-mutated report and the new message commit in one SaveChanges.
        ctx.Attach(report);
        ctx.Entry(report).State = EntityState.Modified;
        ctx.FeedbackMessages.Add(message);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Own reports go entirely — messages cascade off the report FK.
        var ownReports = await ctx.FeedbackReports
            .Where(r => r.UserId == userId)
            .ToListAsync(ct);
        ctx.FeedbackReports.RemoveRange(ownReports);

        var ownReportIds = ownReports.Select(r => r.Id).ToHashSet();

        // Messages left on other people's reports are that report's content — detach the author instead.
        var foreignMessages = await ctx.FeedbackMessages
            .Where(m => m.SenderUserId == userId)
            .ToListAsync(ct);
        foreach (var m in foreignMessages.Where(m => !ownReportIds.Contains(m.FeedbackReportId)))
        {
            ctx.Entry(m).Property(nameof(FeedbackMessage.SenderUserId)).CurrentValue = null;
        }

        // Triage fields on other people's reports.
        var triaged = await ctx.FeedbackReports
            .Where(r => r.ResolvedByUserId == userId || r.AssignedToUserId == userId)
            .ToListAsync(ct);
        foreach (var r in triaged.Where(r => !ownReportIds.Contains(r.Id)))
        {
            if (r.ResolvedByUserId == userId) r.ResolvedByUserId = null;
            if (r.AssignedToUserId == userId) r.AssignedToUserId = null;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        // Reports and messages are unique events: no dedup. UserId and SenderUserId are init-only, so they are set
        // through Entry().Property().CurrentValue.
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var reports = await ctx.FeedbackReports
            .Where(r => r.UserId == sourceUserId || r.AssignedToUserId == sourceUserId
                || r.ResolvedByUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var r in reports)
        {
            if (r.UserId == sourceUserId)
                ctx.Entry(r).Property(nameof(FeedbackReport.UserId)).CurrentValue = targetUserId;
            if (r.AssignedToUserId == sourceUserId)
                r.AssignedToUserId = targetUserId;
            if (r.ResolvedByUserId == sourceUserId)
                r.ResolvedByUserId = targetUserId;
            r.UpdatedAt = updatedAt;
        }

        var messages = await ctx.FeedbackMessages
            .Where(m => m.SenderUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var m in messages)
        {
            ctx.Entry(m).Property(nameof(FeedbackMessage.SenderUserId)).CurrentValue = targetUserId;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
