using Humans.Application.Interfaces.Repositories;
using Humans.Expenses.Contracts;
using Humans.Expenses.Services.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal sealed class ExpenseRepository(IDbContextFactory<ExpensesDbContext> factory)
    : IExpenseRepository
{
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

    public async Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entities = await ctx.ExpenseReports.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Attachment)
            // arch:db-sort-ok dashboard aggregate read — newest-first is the only reasonable default
            .OrderByDescending(r => r.CreatedAt)
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
        // POST should not silently succeed; Withdrawn is already terminal.
        if (r.Status is ExpenseReportStatus.Draft
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

    public async Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
        Instant now, int limit, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseOutboxEvents.AsNoTracking()
            .Where(e => e.ProcessedAt == null
                && !e.FailedPermanently
                && (e.NextRetryAt == null || e.NextRetryAt <= now))
            // arch:db-sort-ok identity-ordered outbox drain — FIFO is the protocol requirement
            .OrderBy(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<HoldedExpenseOutboxEvent?> GetLatestOutboxForReportAsync(
        Guid reportId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseOutboxEvents.AsNoTracking()
            // The create is the push — "did this report reach Holded?". UpdateIncomingDocTag events
            // are legacy no-ops that mark themselves processed, so including them would let one
            // mask a failed create.
            .Where(e => e.ExpenseReportId == reportId
                && e.EventType == HoldedExpenseOutboxEventType.CreateIncomingDoc)
            // arch:db-sort-ok latest-per-report selector (Take)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> CountFailedOutboxAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Only what a finance admin can actually act on. A report withdrawn after approval leaves
        // its queued event behind to be written off later; that report is absent from the review
        // queue and RequeueHoldedPush refuses it, so counting it would leave a banner nobody can
        // clear. Create events only, for the reason on GetLatestOutboxForReportAsync.
        var actionable = ctx.ExpenseReports.AsNoTracking()
            .Where(r => r.Status == ExpenseReportStatus.Approved)
            .Select(r => r.Id);
        return await ctx.HoldedExpenseOutboxEvents.AsNoTracking()
            .CountAsync(e => e.FailedPermanently
                && e.EventType == HoldedExpenseOutboxEventType.CreateIncomingDoc
                && actionable.Contains(e.ExpenseReportId), ct);
    }

    public async Task<bool> RequeueOutboxForReportAsync(
        Guid reportId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Both stuck shapes: written off, and unprocessed-with-an-error (waiting out a backoff).
        // A clean queued event has no error and needs no help.
        var stuck = await ctx.HoldedExpenseOutboxEvents
            .Where(e => e.ExpenseReportId == reportId
                && (e.FailedPermanently || (e.ProcessedAt == null && e.LastError != null)))
            .ToListAsync(ct);
        if (stuck.Count == 0) return false;

        foreach (var e in stuck)
        {
            e.FailedPermanently = false;
            e.ProcessedAt = null;
            e.RetryCount = 0;
            e.LastError = null;
            e.NextRetryAt = null;
        }

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task MarkAttachmentPushedAsync(
        Guid attachmentId, Instant pushedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var a = await ctx.ExpenseAttachments.FirstOrDefaultAsync(x => x.Id == attachmentId, ct);
        if (a is null) return;
        a.HoldedUploadedAt = pushedAt;
        await ctx.SaveChangesAsync(ct);
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

    public async Task SetHoldedContactLinkAsync(
        Guid reportId, string holdedContactId, int? supplierAccountNum,
        Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports.FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null) return;
        r.HoldedContactId = holdedContactId;
        if (supplierAccountNum is not null) r.HoldedSupplierAccountNum = supplierAccountNum;
        r.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task IncrementOutboxRetryAsync(
        Guid outboxEventId, string error, Instant nextRetryAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ev = await ctx.HoldedExpenseOutboxEvents
            .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
        if (ev is null) return;
        ev.RetryCount += 1;
        ev.LastError = error;
        ev.NextRetryAt = nextRetryAt;
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
        // A write-off always follows an attempt that just failed, and that attempt is never counted
        // by IncrementOutboxRetryAsync — the retry path is the branch we did not take. Without this
        // the timeline reads "given up after 9 attempts" while the error says 10.
        ev.RetryCount += 1;
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
