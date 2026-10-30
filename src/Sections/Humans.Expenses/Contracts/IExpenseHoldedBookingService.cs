using Humans.Base.Interfaces;

namespace Humans.Expenses.Contracts;

/// <summary>
/// Kept apart from <see cref="IExpenseReportService"/> so its implementation needs nothing from Finance: Finance
/// depends on this while Expenses depends on Finance's contracts.
/// </summary>
public interface IExpenseHoldedBookingService : IApplicationService
{
    /// <summary>
    /// Every line linked to a Holded purchase document, with the values the push wrote on it. The cap allocation that
    /// trims a line is already applied, so the caller never re-derives it.
    /// </summary>
    Task<IReadOnlyList<ExpenseHoldedBooking>> GetHoldedBookingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every bookable line of every approved report, pushed or not (a line the cap zeroed out gets no document and is
    /// left out), plus any line of another status that already links a document: Holded holds it either way. Outbox
    /// states are read in one query.
    /// </summary>
    Task<IReadOnlyList<ExpenseHoldedReconLine>> GetHoldedReconLinesAsync(CancellationToken ct = default);
}
