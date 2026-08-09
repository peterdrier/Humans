using Humans.Application.Architecture;
using Humans.Expenses.Services.Dtos;

namespace Humans.Expenses.Services;

/// <summary>
/// Cross-section read surface for expense reports. External readers should use
/// this interface instead of the mutation service.
/// </summary>
[SurfaceBudget(7)]
internal interface IExpenseReportServiceRead
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
}
