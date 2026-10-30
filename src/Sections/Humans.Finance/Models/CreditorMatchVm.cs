using Humans.Expenses.Contracts;

namespace Humans.Finance.Models;

/// <summary>What Humans booked to a creditor account against what its ledger holds. Expense docs are the Holded
/// documents a report actually created (the cap can leave lines unbooked), so they must equal the ledger's purchases
/// one for one.</summary>
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
    private const string Purchase = "purchase";
    private const string Payment = "payment";

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

    /// <summary>Purchases count once per journal entry: a document posts one line to the creditor however many lines
    /// it carries. Amounts are net from the member's side.</summary>
    public static CreditorMatchVm Build(
        IReadOnlyCollection<ExpenseReportDto> reports,
        IEnumerable<(string? Type, int EntryNumber, decimal Debit, decimal Credit)> ledger)
    {
        var booked = reports.Where(r => r.HoldedDocIds.Count > 0).ToList();
        var lines = ledger.ToList();
        var purchases = lines.Where(l => string.Equals(l.Type, Purchase, StringComparison.OrdinalIgnoreCase)).ToList();
        var payments = lines.Where(l => string.Equals(l.Type, Payment, StringComparison.OrdinalIgnoreCase)).ToList();

        return new CreditorMatchVm(
            ExpenseDocCount: booked.Sum(r => r.HoldedDocIds.Count),
            ExpenseAmount: booked.Sum(r => r.Payable),
            PurchaseCount: purchases.Select(l => l.EntryNumber).Distinct().Count(),
            PurchaseAmount: purchases.Sum(l => l.Credit - l.Debit),
            PaymentCount: payments.Count,
            PaymentAmount: payments.Sum(l => l.Debit - l.Credit),
            OtherLedgerCount: lines.Count - purchases.Count - payments.Count,
            ApprovedNotBookedCount: reports.Count(r =>
                r.Status == ExpenseReportStatus.Approved && r.HoldedDocIds.Count == 0 && r.Payable > 0m));
    }
}
