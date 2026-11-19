using Humans.Finance.Contracts;

namespace Humans.Finance.Models;

/// <summary>
/// One creditor account's slice of the expense reconciliation: the same rule as /Finance/ExpenseRecon, never a second
/// count. Amounts are from the member's side, so positive is owed to the member.
/// </summary>
internal sealed record CreditorMatchVm(
    decimal ExpensePurchases,
    decimal SepaPaid,
    decimal UnclaimedPurchases,
    decimal Other,
    int MissingLines,
    int UnclaimedPurchaseCount,
    int DuplicatedLines,
    int UnmatchedTransfers)
{
    /// <summary>Null when the reconciliation is unavailable, or has nothing to say about the account: no member with
    /// an expense line is on it and no expense purchase sits on it unclaimed.</summary>
    public static CreditorMatchVm? For(ExpenseReconReport recon, int supplierAccountNum)
    {
        if (!recon.IsAvailable) return null;
        var orphans = recon.Orphans.Where(o => o.AccountNum == supplierAccountNum).ToList();
        // Two members bound to one account each get a row: the ledger split is the account's, the lines are theirs.
        var members = recon.Members.Where(m => m.SupplierAccountNum == supplierAccountNum).ToList();
        if (members.Count == 0)
            return orphans.Count == 0
                ? null
                : new CreditorMatchVm(0m, 0m, orphans.Sum(o => o.Amount), 0m, 0, orphans.Count, 0, 0);

        var member = members[0];
        var users = members.Select(m => m.UserId).ToHashSet();
        return new CreditorMatchVm(
            -member.ExpensePurchases,
            -member.SepaPaid,
            -member.UnclaimedPurchases,
            -member.OtherTotal,
            members.Sum(m => m.MissingLines),
            member.UnclaimedPurchaseCount,
            recon.Lines.Count(l => users.Contains(l.CreditorUserId) && l.Status == ExpenseReconLineStatus.Duplicated),
            member.UnmatchedTransfers.Count);
    }

    /// <summary>Empty when everything matches.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (MissingLines > 0)
                warnings.Add($"{MissingLines} approved expense line(s) not in the ledger");
            if (DuplicatedLines > 0)
                warnings.Add($"{DuplicatedLines} expense line(s) booked more than once");
            if (UnclaimedPurchaseCount > 0)
                warnings.Add($"{UnclaimedPurchaseCount} expense purchase(s) in the ledger that no report line claims");
            if (UnmatchedTransfers > 0)
                warnings.Add($"{UnmatchedTransfers} booked SEPA transfer(s) not found in the ledger");
            return warnings;
        }
    }

    public bool HasWarnings => Warnings.Count > 0;
}
