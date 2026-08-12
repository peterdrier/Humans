using Humans.Application.Architecture;
using NodaTime;

namespace Humans.Expenses.Contracts;

/// <summary>
/// Cross-section read surface for expense reports. External readers should use
/// this interface instead of the mutation service.
/// </summary>
[SurfaceBudget(8)]
public interface IExpenseReportServiceRead
{
    Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ExpenseHoldedTimeline?> GetHoldedTimelineAsync(
        ExpenseReportDto report, CancellationToken ct = default);

    /// <summary>
    /// Returns the report that owns the given attachment (via its line), with
    /// Lines populated. Returns null if the attachment doesn't belong to any
    /// line or the report is gone.
    /// </summary>
    Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default);

    Task<ExpenseAttachmentDownload?> TryReadAttachmentAsync(
        ExpenseReportDto owningReport,
        Guid attachmentId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(CancellationToken ct = default);

    /// <summary>All expense reports, all statuses — dashboard/aggregate reads sum client-side (~500-user scale).</summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default);
}

public sealed record ExpenseAttachmentDownload(
    byte[] Bytes,
    string ContentType,
    string OriginalFileName);

/// <summary>Round-trip timeline for the submitter, derived from the cached Holded creditor ledger.</summary>
public sealed record ExpenseHoldedTimeline(
    bool RegisteredInHolded,
    decimal OwedToMember,
    decimal MemberRegisteredTotal,   // sum of this member's registered-but-unpaid ER totals
    decimal OtherAmount,             // max(0, OwedToMember - MemberRegisteredTotal): fronted / adjustments
    bool Paid,
    LocalDate? PaidOn,
    decimal TotalPaid);
