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
}
