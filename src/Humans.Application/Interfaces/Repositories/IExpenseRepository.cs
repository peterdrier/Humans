using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

public interface IExpenseRepository
{
    // Reads
    Task<ExpenseReport?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ExpenseReport?> GetByIdWithLinesAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetByStatusAsync(
        ExpenseReportStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetByCategoryIdsAndStatusAsync(
        IReadOnlyCollection<Guid> categoryIds,
        ExpenseReportStatus status,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetForReviewQueueAsync(CancellationToken ct = default);
    Task<ExpenseAttachment?> GetAttachmentByIdAsync(Guid id, CancellationToken ct = default);

    // Writes — atomic per-method, all inside one short-lived DbContext.
    Task AddDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task UpdateDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task<bool> AddLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    Task<bool> UpdateLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    Task<bool> RemoveLineAsync(
        Guid reportId, Guid lineId, CancellationToken ct = default);
    Task<Guid> AddAttachmentAsync(
        ExpenseAttachment attachment, CancellationToken ct = default);
    Task RemoveAttachmentAsync(Guid id, CancellationToken ct = default);
    Task SetLineAttachmentAsync(
        Guid lineId, Guid? attachmentId, CancellationToken ct = default);

    Task<bool> SubmitAsync(
        Guid reportId,
        string payeeName, string payeeIban,
        NodaTime.Instant submittedAt,
        CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, NodaTime.Instant updatedAt, CancellationToken ct = default);

    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid actorUserId,
        NodaTime.Instant endorsedAt, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId,
        Guid? overrideCategoryId,
        NodaTime.Instant approvedAt,
        Guid outboxEventId,
        CancellationToken ct = default);

    Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    Task<bool> CategoryOverrideAsync(
        Guid reportId, Guid actorUserId,
        Guid newCategoryId,
        NodaTime.Instant overriddenAt,
        Guid outboxEventId,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds,
        NodaTime.Instant sepaSentAt,
        CancellationToken ct = default);

    Task<bool> MarkPaidAsync(
        Guid reportId, NodaTime.Instant paidAt, CancellationToken ct = default);

    // Outbox
    Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
        int limit, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetFailedPermanentlyAsync(
        CancellationToken ct = default);
    Task SetHoldedDocIdAsync(
        Guid reportId, string holdedDocId,
        Guid outboxEventId, NodaTime.Instant processedAt,
        CancellationToken ct = default);
    Task IncrementOutboxRetryAsync(
        Guid outboxEventId, string error, CancellationToken ct = default);
    Task MarkOutboxFailedPermanentlyAsync(
        Guid outboxEventId, string error,
        NodaTime.Instant processedAt, CancellationToken ct = default);
    Task MarkOutboxProcessedAsync(
        Guid outboxEventId, NodaTime.Instant processedAt, CancellationToken ct = default);
}
