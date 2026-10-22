using Humans.Expenses.Contracts;

namespace Humans.Expenses.Services;

/// <summary>
/// Greedy in <c>SortOrder</c>: each line books in full until the payable runs out. The whole report books to one
/// account, so where the reduction lands is presentation only, and this keeps each Holded doc matching its receipt.
/// The push, the detail view and the audit message all read this, so they never disagree.
/// </summary>
internal static class PayableAllocation
{
    internal sealed record LineAllocation(ExpenseLineDto Line, decimal Booked)
    {
        /// <summary>Books, but below the receipt amount.</summary>
        internal bool Trimmed => Booked > 0m && Booked < Line.Amount;

        /// <summary>No Holded doc, no attachment upload.</summary>
        internal bool Skipped => Booked <= 0m && Line.Amount > 0m;
    }

    internal static IReadOnlyList<LineAllocation> Allocate(ExpenseReportDto report)
    {
        var remaining = report.Payable;
        var allocations = new List<LineAllocation>();
        foreach (var line in report.Lines
                     .Where(l => l.ParentLineId is null && !l.Declined)
                     .OrderBy(l => l.SortOrder))
        {
            var booked = Math.Max(0m, Math.Min(line.Amount, remaining));
            remaining -= booked;
            allocations.Add(new LineAllocation(line, booked));
        }
        return allocations;
    }
}
