using Humans.Expenses.Contracts;

namespace Humans.Expenses.Services;

/// <summary>
/// How the authorized cap distributes over the bookable lines: each line books in full, in
/// <c>SortOrder</c>, until the payable runs out — the line the cap lands inside is trimmed, and
/// every line after it is skipped. Where the reduction lands is presentation only (the whole
/// report books to one category account), so greedy-in-order is as correct as anything and keeps
/// each Holded doc matching its receipt. The push, the detail view, and the audit message all
/// read this one allocation so they can never disagree.
/// </summary>
internal static class PayableAllocation
{
    internal sealed record LineAllocation(ExpenseLineDto Line, decimal Booked)
    {
        /// <summary>The cap landed inside this line: it books, but below the receipt amount.</summary>
        internal bool Trimmed => Booked > 0m && Booked < Line.Amount;

        /// <summary>The cap was spent before this line: no Holded doc, no attachment upload.</summary>
        internal bool Skipped => Booked <= 0m && Line.Amount > 0m;
    }

    internal static IReadOnlyList<LineAllocation> Allocate(ExpenseReportDto report)
    {
        var remaining = report.Payable;
        var allocations = new List<LineAllocation>();
        foreach (var line in report.Lines
                     .Where(l => l.ParentLineId is null)
                     .OrderBy(l => l.SortOrder))
        {
            var booked = Math.Max(0m, Math.Min(line.Amount, remaining));
            remaining -= booked;
            allocations.Add(new LineAllocation(line, booked));
        }
        return allocations;
    }
}
