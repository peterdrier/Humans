using Humans.Base.Interfaces;

namespace Humans.Expenses.Contracts;

public interface IExpenseReportService : IApplicationService
{
    Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ExpenseHoldedTimeline?> GetHoldedTimelineAsync(
        ExpenseReportDto report, CancellationToken ct = default);

    /// <summary>With lines. Null when no line holds the attachment or the report is gone.</summary>
    Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default);

    Task<ExpenseAttachmentDownload?> TryReadAttachmentAsync(
        ExpenseReportDto owningReport,
        Guid attachmentId,
        CancellationToken ct = default);

    /// <summary>
    /// A finance admin sees every non-draft, non-withdrawn report; anyone else their own (merged accounts included)
    /// plus those booked to a budget category they coordinate.
    /// </summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        Guid viewerUserId, bool isFinanceAdmin, CancellationToken ct = default);
}
