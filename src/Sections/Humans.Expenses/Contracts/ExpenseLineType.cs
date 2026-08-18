namespace Humans.Expenses.Contracts;

/// <summary>
/// Kind of expense line. <see cref="Receipt"/> lines require an attachment at submit time;
/// travel lines (<see cref="Mileage"/> / <see cref="PerDiem"/>) are justified by the trip, not a receipt.
/// <see cref="Invoice"/> is a supplier invoice (ZZP / autónomo payee) — it requires the invoice file
/// attached and can carry proof rows (Receipt lines with <c>ParentLineId</c> set) that back it up
/// for review but are never booked or pushed to Holded.
/// </summary>
public enum ExpenseLineType
{
    Receipt = 0,
    Mileage,
    PerDiem,
    Invoice
}
