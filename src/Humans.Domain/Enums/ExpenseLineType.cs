namespace Humans.Domain.Enums;

/// <summary>
/// Kind of expense line. <see cref="Receipt"/> lines require an attachment at submit time;
/// travel lines (<see cref="Mileage"/> / <see cref="PerDiem"/>) are justified by the trip, not a receipt.
/// </summary>
public enum ExpenseLineType
{
    Receipt = 0,
    Mileage,
    PerDiem
}
