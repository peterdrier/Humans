using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

[Section("Expenses")]

public interface IExpenseRepository : IRepository
{
    // Reads — all return fully-populated DTOs (Lines + Attachment metadata always included).
    // EF entity types stay inside Infrastructure; the Application layer sees only DTOs.
    Task<ExpenseReportDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetByStatusAsync(
        ExpenseReportStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetByCategoryIdsAndStatusAsync(
        IReadOnlyCollection<Guid> categoryIds,
        ExpenseReportStatus status,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForReviewQueueAsync(CancellationToken ct = default);
    /// <summary>
    /// Resolves the report id that owns the given attachment via the line that
    /// references it. Returns null if no line currently points at the attachment
    /// (orphan attachment or unknown id).
    /// </summary>
    Task<Guid?> GetReportIdByAttachmentIdAsync(Guid attachmentId, CancellationToken ct = default);

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

    Task<IReadOnlyList<Guid>> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds,
        NodaTime.Instant sepaSentAt,
        CancellationToken ct = default);

    Task<bool> MarkPaidAsync(
        Guid reportId, NodaTime.Instant paidAt, CancellationToken ct = default);

    // Outbox
    Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
        int limit, CancellationToken ct = default);
    /// <summary>
    /// Persists the freshly-issued Holded document id on the report. Caller
    /// invokes this immediately after <c>IHoldedClient.CreatePurchaseDocumentAsync</c>
    /// returns — that way a transient failure during attachment upload (which runs
    /// after) does not cause the outbox event to retry the create call and produce
    /// a duplicate Holded document. Marking the outbox event processed is a
    /// separate <see cref="MarkOutboxProcessedAsync"/> call that runs only after
    /// the full create + upload chain succeeds.
    /// </summary>
    Task SetHoldedDocIdAsync(
        Guid reportId, string holdedDocId, NodaTime.Instant updatedAt,
        CancellationToken ct = default);
    Task IncrementOutboxRetryAsync(
        Guid outboxEventId, string error, CancellationToken ct = default);
    Task MarkOutboxFailedPermanentlyAsync(
        Guid outboxEventId, string error,
        NodaTime.Instant processedAt, CancellationToken ct = default);
    Task MarkOutboxProcessedAsync(
        Guid outboxEventId, NodaTime.Instant processedAt, CancellationToken ct = default);
}
