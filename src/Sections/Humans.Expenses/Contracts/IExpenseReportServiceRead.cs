namespace Humans.Expenses.Contracts;

/// <summary>
/// Read-only cross-section surface for expense reports. Backdoor's finance read API
/// (peterdrier/Humans#1838) is the first external consumer — everything here is exactly what
/// <c>IExpenseReportService</c> already exposed internally; nothing new was written to add this.
/// </summary>
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

    /// <summary>
    /// The review queue as <paramref name="viewerUserId"/> may see it: every non-draft,
    /// non-withdrawn report for a finance admin; otherwise the viewer's own reports plus those
    /// booked to a budget category they coordinate.
    /// </summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        Guid viewerUserId, bool isFinanceAdmin, CancellationToken ct = default);
}
