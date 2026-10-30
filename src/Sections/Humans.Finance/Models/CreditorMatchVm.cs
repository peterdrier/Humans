namespace Humans.Finance.Models;

/// <summary>Computed by <see cref="Services.CreditorMatchService"/>; the view only reads it.</summary>
internal sealed record CreditorMatchVm(
    int ExpenseDocCount,
    decimal ExpenseAmount,
    int PurchaseCount,
    decimal PurchaseAmount,
    int PaymentCount,
    decimal PaymentAmount,
    int OtherLedgerCount,
    int ApprovedNotBookedCount)
{
    public bool CountsMatch => ExpenseDocCount == PurchaseCount;
    public bool AmountsMatch => ExpenseAmount == PurchaseAmount;

    /// <summary>Empty when everything matches.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (!CountsMatch)
                warnings.Add($"{ExpenseDocCount} expense docs vs {PurchaseCount} ledger purchases");
            if (!AmountsMatch)
                warnings.Add(FormattableString.Invariant(
                    $"expense total {ExpenseAmount:#,##0.00} vs ledger purchases {PurchaseAmount:#,##0.00}"));
            if (OtherLedgerCount > 0)
                warnings.Add($"{OtherLedgerCount} ledger entries that are neither purchase nor payment");
            if (ApprovedNotBookedCount > 0)
                warnings.Add($"{ApprovedNotBookedCount} approved report(s) not in Holded yet");
            return warnings;
        }
    }

    public bool HasWarnings => Warnings.Count > 0;
}
