using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Models;
using Humans.Holded.Contracts;

namespace Humans.Finance.Services;

/// <summary>What Humans booked to a member's creditor account against what its ledger holds. Expense docs are the
/// Holded documents a report actually created (the cap can leave lines unbooked), so they must equal the ledger's
/// purchases one for one.</summary>
internal sealed class CreditorMatchService(IHoldedService holded, IExpenseReportService expenseReports)
    : IApplicationService
{
    private const string Purchase = "purchase";
    private const string Payment = "payment";

    /// <summary>Single-bound accounts with ledger lines only: an org vendor's purchases never come from expense
    /// reports, and a colliding row has no one owner to reconcile.</summary>
    public async Task<IReadOnlyDictionary<int, CreditorMatchVm>> GetForMemberAccountsAsync(
        IEnumerable<HoldedCreditorAccountRow> rows, Guid viewerUserId, CancellationToken ct)
    {
        var reports = await GetReportsAsync(viewerUserId, ct);
        var matches = new Dictionary<int, CreditorMatchVm>();
        foreach (var row in rows.Where(r => r.Bindings.Count == 1 && r.Balance.HasValue))
            matches[row.SupplierAccountNum] =
                await MatchAsync(row.SupplierAccountNum, reports, row.Bindings[0].UserId, ct);
        return matches;
    }

    public async Task<CreditorMatchVm> GetAsync(int supplierAccountNum, Guid viewerUserId, CancellationToken ct) =>
        await MatchAsync(supplierAccountNum, await GetReportsAsync(viewerUserId, ct), boundUserId: null, ct);

    private async Task<CreditorMatchVm> MatchAsync(
        int supplierAccountNum, IReadOnlyList<ExpenseReportDto> reports, Guid? boundUserId, CancellationToken ct) =>
        Match(
            ReportsForAccount(supplierAccountNum, reports, boundUserId),
            (await holded.GetLedgerLinesAsync(supplierAccountNum, ct))
                .Select(l => (l.Type, l.EntryNumber, l.Debit, l.Credit)));

    /// <summary>The push records a report's account only after its documents exist, so an approval still waiting in
    /// the outbox has none yet. It is the account owner's by submitter: the bound member, or whoever submitted a
    /// report already booked here.</summary>
    internal static List<ExpenseReportDto> ReportsForAccount(
        int supplierAccountNum, IReadOnlyList<ExpenseReportDto> reports, Guid? boundUserId)
    {
        var owners = reports.Where(r => r.HoldedSupplierAccountNum == supplierAccountNum)
            .Select(r => r.SubmitterUserId).ToHashSet();
        if (boundUserId is { } bound) owners.Add(bound);
        return reports
            .Where(r => r.HoldedSupplierAccountNum == supplierAccountNum
                        || (r.HoldedSupplierAccountNum is null && owners.Contains(r.SubmitterUserId)))
            .ToList();
    }

    // The finance-admin review queue is every submitted report.
    private async Task<IReadOnlyList<ExpenseReportDto>> GetReportsAsync(Guid viewerUserId, CancellationToken ct) =>
        await expenseReports.GetReviewQueueAsync(viewerUserId, isFinanceAdmin: true, ct);

    /// <summary>Purchases count once per journal entry: a document posts one line to the creditor however many lines
    /// it carries. Amounts are net from the member's side.</summary>
    internal static CreditorMatchVm Match(
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
