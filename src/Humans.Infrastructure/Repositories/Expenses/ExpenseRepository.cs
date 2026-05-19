using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Expenses;

internal sealed class ExpenseRepository(IDbContextFactory<HumansDbContext> factory, ILogger<ExpenseRepository> logger)
    : IExpenseRepository
{
    private readonly ILogger<ExpenseRepository> _logger = logger;

    public async Task<ExpenseReportDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entity = await ctx.ExpenseReports.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Attachment)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return entity is null ? null : ExpenseReportMapper.ToDto(entity);
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entities = await ctx.ExpenseReports.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Attachment)
            .Where(r => r.SubmitterUserId == submitterUserId)
            // arch:db-sort-ok submitter's own list — newest-first paging-friendly default
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ExpenseReportMapper.ToDto).ToList();
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetByStatusAsync(
        ExpenseReportStatus status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entities = await ctx.ExpenseReports.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Attachment)
            .Where(r => r.Status == status)
            // arch:db-sort-ok FIFO poll order — oldest SubmittedAt first feeds the SepaSent→Paid poller deterministically
            .OrderBy(r => r.SubmittedAt ?? r.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ExpenseReportMapper.ToDto).ToList();
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetByCategoryIdsAndStatusAsync(
        IReadOnlyCollection<Guid> categoryIds,
        ExpenseReportStatus status,
        CancellationToken ct = default)
    {
        if (categoryIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entities = await ctx.ExpenseReports.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Attachment)
            .Where(r => r.Status == status && categoryIds.Contains(r.BudgetCategoryId))
            // arch:db-sort-ok coordinator queue FIFO — oldest pending submissions surface first
            .OrderBy(r => r.SubmittedAt ?? r.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ExpenseReportMapper.ToDto).ToList();
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetForReviewQueueAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entities = await ctx.ExpenseReports.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Attachment)
            .Where(r => r.Status != ExpenseReportStatus.Draft
                     && r.Status != ExpenseReportStatus.Withdrawn)
            // arch:db-sort-ok finance review queue — newest submissions on top so reviewers see fresh work first
            .OrderByDescending(r => r.SubmittedAt ?? r.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ExpenseReportMapper.ToDto).ToList();
    }

    public async Task<Guid?> GetReportIdByAttachmentIdAsync(
        Guid attachmentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseLines.AsNoTracking()
            .Where(l => l.AttachmentId == attachmentId)
            .Select(l => (Guid?)l.ExpenseReportId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddDraftAsync(ExpenseReport report, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.ExpenseReports.Add(report);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateDraftAsync(ExpenseReport report, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.ExpenseReports
            .FirstOrDefaultAsync(r => r.Id == report.Id, ct);
        if (tracked is null || tracked.Status != ExpenseReportStatus.Draft) return;
        tracked.BudgetCategoryId = report.BudgetCategoryId;
        tracked.BudgetYearId = report.BudgetYearId;
        tracked.Note = report.Note;
        tracked.UpdatedAt = report.UpdatedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> AddLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var report = await ctx.ExpenseReports
            .FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return false;
        line.ExpenseReportId = reportId;
        line.SortOrder = await ctx.ExpenseLines.CountAsync(l => l.ExpenseReportId == reportId, ct);
        report.Total += line.Amount;
        ctx.ExpenseLines.Add(line);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var report = await ctx.ExpenseReports
            .FirstOrDefaultAsync(r => r.Id == reportId, ct);
        var tracked = await ctx.ExpenseLines
            .FirstOrDefaultAsync(l => l.Id == line.Id && l.ExpenseReportId == reportId, ct);
        if (report is null || tracked is null) return false;
        report.Total = report.Total - tracked.Amount + line.Amount;
        tracked.Description = line.Description;
        tracked.Amount = line.Amount;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveLineAsync(
        Guid reportId, Guid lineId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var report = await ctx.ExpenseReports
            .FirstOrDefaultAsync(r => r.Id == reportId, ct);
        var tracked = await ctx.ExpenseLines
            .FirstOrDefaultAsync(l => l.Id == lineId && l.ExpenseReportId == reportId, ct);
        if (report is null || tracked is null) return false;
        ctx.ExpenseLines.Remove(tracked);
        report.Total = report.Total - tracked.Amount;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Guid> AddAttachmentAsync(
        ExpenseAttachment attachment, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.ExpenseAttachments.Add(attachment);
        await ctx.SaveChangesAsync(ct);
        return attachment.Id;
    }

    public async Task RemoveAttachmentAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var att = await ctx.ExpenseAttachments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (att is null) return;
        ctx.ExpenseAttachments.Remove(att);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SetLineAttachmentAsync(
        Guid lineId, Guid? attachmentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var line = await ctx.ExpenseLines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return;
        line.AttachmentId = attachmentId;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> SubmitAsync(
        Guid reportId, string payeeName, string payeeIban,
        Instant submittedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports
            .FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null || r.Status != ExpenseReportStatus.Draft) return false;
        r.Status = ExpenseReportStatus.Submitted;
        r.PayeeName = payeeName;
        r.PayeeIban = payeeIban;
        r.SubmittedAt = submittedAt;
        r.UpdatedAt = submittedAt;
        r.LastRejectionReason = null;
        r.LastRejectedByUserId = null;
        r.LastRejectedAt = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> WithdrawAsync(
        Guid reportId, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports
            .FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null) return false;
        // Withdraw is valid only from Submitted/CoordinatorEndorsed/Approved per section invariant.
        // Draft has no UI Withdraw path (use Delete-while-Draft when that ships) and a direct
        // POST should not silently succeed; post-payout terminal states stay locked.
        if (r.Status is ExpenseReportStatus.Draft
                     or ExpenseReportStatus.SepaSent
                     or ExpenseReportStatus.Paid
                     or ExpenseReportStatus.Withdrawn) return false;
        r.Status = ExpenseReportStatus.Withdrawn;
        r.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid actorUserId, Instant endorsedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports
            .FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null || r.Status != ExpenseReportStatus.Submitted) return false;
        r.Status = ExpenseReportStatus.CoordinatorEndorsed;
        r.CoordinatorEndorsedByUserId = actorUserId;
        r.CoordinatorEndorsedAt = endorsedAt;
        r.UpdatedAt = endorsedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        Instant rejectedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports
            .FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null || r.Status != ExpenseReportStatus.Submitted) return false;
        r.Status = ExpenseReportStatus.Draft;
        r.LastRejectionReason = reason;
        r.LastRejectedByUserId = actorUserId;
        r.LastRejectedAt = rejectedAt;
        r.UpdatedAt = rejectedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        Instant approvedAt, Guid outboxEventId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports
            .FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null) return false;
        if (r.Status is not (ExpenseReportStatus.Submitted
                             or ExpenseReportStatus.CoordinatorEndorsed)) return false;

        r.Status = ExpenseReportStatus.Approved;
        r.ApprovedByUserId = actorUserId;
        r.ApprovedAt = approvedAt;
        r.UpdatedAt = approvedAt;
        if (overrideCategoryId is { } cat) r.BudgetCategoryId = cat;

        ctx.HoldedExpenseOutboxEvents.Add(new HoldedExpenseOutboxEvent
        {
            Id = outboxEventId,
            ExpenseReportId = r.Id,
            EventType = HoldedExpenseOutboxEventType.CreateIncomingDoc,
            OccurredAt = approvedAt
        });

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        Instant rejectedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports
            .FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null) return false;
        if (r.Status is not (ExpenseReportStatus.Submitted
                             or ExpenseReportStatus.CoordinatorEndorsed)) return false;
        r.Status = ExpenseReportStatus.Draft;
        r.LastRejectionReason = reason;
        r.LastRejectedByUserId = actorUserId;
        r.LastRejectedAt = rejectedAt;
        r.CoordinatorEndorsedAt = null;
        r.CoordinatorEndorsedByUserId = null;
        r.UpdatedAt = rejectedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Guid>> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds, Instant sepaSentAt,
        CancellationToken ct = default)
    {
        if (reportIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.ExpenseReports
            .Where(r => reportIds.Contains(r.Id) && r.Status == ExpenseReportStatus.Approved)
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            r.Status = ExpenseReportStatus.SepaSent;
            r.SepaSentAt = sepaSentAt;
            r.UpdatedAt = sepaSentAt;
        }
        await ctx.SaveChangesAsync(ct);
        return rows.Select(r => r.Id).ToList();
    }

    public async Task<bool> MarkPaidAsync(
        Guid reportId, Instant paidAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports
            .FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null || r.Status != ExpenseReportStatus.SepaSent) return false;
        r.Status = ExpenseReportStatus.Paid;
        r.PaidAt = paidAt;
        r.UpdatedAt = paidAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
        int limit, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseOutboxEvents.AsNoTracking()
            .Where(e => e.ProcessedAt == null && !e.FailedPermanently)
            // arch:db-sort-ok identity-ordered outbox drain — FIFO is the protocol requirement
            .OrderBy(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task SetHoldedDocIdAsync(
        Guid reportId, string holdedDocId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports.FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null) return;
        r.HoldedDocId = holdedDocId;
        r.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task IncrementOutboxRetryAsync(
        Guid outboxEventId, string error, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ev = await ctx.HoldedExpenseOutboxEvents
            .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
        if (ev is null) return;
        ev.RetryCount += 1;
        ev.LastError = error;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkOutboxFailedPermanentlyAsync(
        Guid outboxEventId, string error, Instant processedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ev = await ctx.HoldedExpenseOutboxEvents
            .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
        if (ev is null) return;
        ev.FailedPermanently = true;
        ev.LastError = error;
        ev.ProcessedAt = processedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkOutboxProcessedAsync(
        Guid outboxEventId, Instant processedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ev = await ctx.HoldedExpenseOutboxEvents
            .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
        if (ev is null) return;
        ev.ProcessedAt = processedAt;
        await ctx.SaveChangesAsync(ct);
    }
}
