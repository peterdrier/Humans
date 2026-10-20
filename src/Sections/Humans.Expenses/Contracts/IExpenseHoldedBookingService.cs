using Humans.Base.Interfaces;

namespace Humans.Expenses.Contracts;

/// <summary>
/// Read-only: what each expense line wrote on its Holded purchase document. Finance's
/// duplicate-document check (/Finance/HoldedDuplicates) is the consumer. Kept apart from
/// <see cref="IExpenseReportService"/> so its implementation needs nothing from Finance:
/// Finance depends on this, Expenses depends on Finance's contracts, and the two never resolve
/// each other.
/// </summary>
public interface IExpenseHoldedBookingService : IApplicationService
{
    /// <summary>
    /// Every expense line that links to a Holded purchase document, with the values the push wrote
    /// on that document. Finance matches Holded's purchase list against these; the allocation that
    /// trims a line is applied here so the caller never re-derives it.
    /// </summary>
    Task<IReadOnlyList<ExpenseHoldedBooking>> GetHoldedBookingsAsync(CancellationToken ct = default);
}
