namespace Humans.Expenses.Contracts;

/// <summary>
/// Public contract for expense reports (read-only). Backdoor's finance read API
/// (peterdrier/Humans#1838) consumes it; it exposes exactly what <c>IExpenseReportManagement</c>
/// exposes internally.
/// </summary>
public interface IExpenseReportService
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

    /// <summary>
    /// The review queue as <paramref name="viewerUserId"/> may see it: every non-draft,
    /// non-withdrawn report for a finance admin; otherwise the viewer's own reports plus those
    /// booked to a budget category they coordinate.
    /// </summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        Guid viewerUserId, bool isFinanceAdmin, CancellationToken ct = default);
}
