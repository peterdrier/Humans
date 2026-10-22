namespace Humans.Expenses.Contracts;

/// <summary>
/// <see cref="Receipt"/> and <see cref="Invoice"/> lines need an attachment at submit; travel lines are justified by
/// the trip. An Invoice can carry proof rows (Receipt lines with <c>ParentLineId</c>) that are reviewed but never
/// booked.
/// </summary>
public enum ExpenseLineType
{
    Receipt = 0,
    Mileage,
    PerDiem,
    Invoice
}
