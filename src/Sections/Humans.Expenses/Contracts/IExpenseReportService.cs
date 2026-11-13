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
    /// All non-draft, non-withdrawn review candidates. Callers authorize reports before exposing them;
    /// browser and API listings also retain their active-year coordinator display scope.
    /// </summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(CancellationToken ct = default);
}
