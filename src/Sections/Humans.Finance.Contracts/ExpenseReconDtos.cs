using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>
/// What Humans approved against what the Holded ledger mirror holds: expense lines, stray marked purchases, and each
/// member's creditor account explained line by line. Built from local data only; <see cref="Year"/> narrows lines and
/// orphans, never members, whose ledgers are all-time.
/// </summary>
/// <remarks><paramref name="UnavailableReason"/> is set, and the lists are empty, when the ledger mirror has never synced, so
/// nothing can be judged.</remarks>
public sealed record ExpenseReconReport(
    int? Year,
    Instant? MirrorSyncedAt,
    string? UnavailableReason,
    IReadOnlyList<int> Years,
    IReadOnlyList<ExpenseReconLine> Lines,
    IReadOnlyList<ExpenseReconOrphan> Orphans,
    IReadOnlyList<ExpenseReconMember> Members,
    ExpenseReconTotals Totals)
{
    public bool IsAvailable => UnavailableReason is null;
}

/// <summary><see cref="Problems"/> is what the "0 problems" line counts: problem lines and orphans.
/// <see cref="Warnings"/> counts members with a real mismatch: advisory only, never blocking anything.
/// <paramref name="LedgerPurchases"/> counts the ledger purchase lines the report judged: matched, duplicate and
/// orphan.</summary>
public sealed record ExpenseReconTotals(
    int Reports,
    int Lines,
    int LedgerPurchases,
    int ProblemLines,
    int DuplicateLines,
    decimal ExcessAmount,
    int Orphans,
    int MembersOff)
{
    public int Problems => ProblemLines + Orphans;

    public int Warnings => MembersOff;
}

public enum ExpenseReconLineStatus
{
    /// <summary>A purchase line on the member's account carries the line's description and amount.</summary>
    Booked,
    /// <summary>No ledger line yet and nothing has gone wrong: the push is queued or retrying, or it landed after
    /// the ledger mirror's last sweep. Not a problem.</summary>
    Pending,
    /// <summary>Approved, but no purchase line on the member's account carries it: see
    /// <see cref="ExpenseReconLine.PushState"/>.</summary>
    Missing,
    /// <summary>Booked, and the account carries more purchase lines with its description and amount than there are
    /// expense lines.</summary>
    Duplicated,
}

/// <summary>
/// One expected purchase. <paramref name="LineId"/> is null for a legacy report-level document.
/// <paramref name="PushState"/> is the report's Holded outbox state (<c>Queued</c>, <c>Retrying</c>, <c>Failed</c>,
/// <c>Pushed</c>, <c>NotQueued</c>, <c>NotConfigured</c>). <paramref name="HoldedDocId"/> is the pointer the push
/// stored, shown for reference only: matching never reads it. The <c>Ledger*</c> fields name the purchase line that
/// matched, null when none did; <paramref name="ExcessAmount"/> is the sum of the extra lines.
/// </summary>
public sealed record ExpenseReconLine(
    Guid ExpenseReportId,
    Guid? LineId,
    Guid CreditorUserId,
    string MemberName,
    string Description,
    decimal ExpectedAmount,
    LocalDate DocumentDate,
    string PushState,
    ExpenseReconLineStatus Status,
    string? HoldedDocId,
    int? LedgerAccountNum,
    int? LedgerEntryNumber,
    LocalDate? LedgerDate,
    IReadOnlyList<int> DuplicateEntryNumbers,
    decimal ExcessAmount)
{
    public bool IsProblem => Status is not (ExpenseReconLineStatus.Booked or ExpenseReconLineStatus.Pending);
}

public enum ExpenseReconOrphanKind
{
    /// <summary>On an account a member is bound to, but no expense line claims it.</summary>
    NoMatchingLine,
    /// <summary>On an account no member is bound to.</summary>
    UnboundContact,
}

/// <summary>A purchase line on a creditor account carrying the push's marker (<c>ER: …</c>, or the older
/// <c>… — expense report &lt;id&gt;</c>) that no expense line claims and none counts as its duplicate.
/// <paramref name="Amount"/> is credit − debit: positive is owed to the account.</summary>
public sealed record ExpenseReconOrphan(
    int AccountNum,
    int EntryNumber,
    LocalDate Date,
    string Description,
    decimal Amount,
    ExpenseReconOrphanKind Kind);

public enum ExpenseReconMemberStatus
{
    /// <summary>Every line matched, every marked purchase claimed, every booked SEPA transfer found.</summary>
    Balanced,
    /// <summary>A real mismatch: see <see cref="ExpenseReconMember.MissingLines"/>,
    /// <see cref="ExpenseReconMember.UnclaimedPurchaseCount"/> and <see cref="ExpenseReconMember.UnmatchedTransfers"/>.</summary>
    Off,
    /// <summary>No creditor binding resolving to an account, so there is no ledger to compare.</summary>
    NoAccount,
}

/// <summary>
/// The account the member's bound contact is on, its ledger split four ways. Amounts use Holded's sign, Σdebit −
/// Σcredit (negative is owed to the member), and add up: <see cref="LedgerBalance"/> = <see cref="ExpensePurchases"/>
/// + <see cref="SepaPaid"/> + <see cref="UnclaimedPurchases"/> + <see cref="OtherTotal"/>.
/// <see cref="OtherMovements"/> (unmarked purchases, journal entries, payments no booked SEPA transfer accounts for,
/// collections) are information, never a warning. <see cref="OpenTransfers"/> are generated and neither booked nor
/// withdrawn: money that may have left the bank but is not in the ledger yet.
/// </summary>
public sealed record ExpenseReconMember(
    Guid UserId,
    string MemberName,
    int? SupplierAccountNum,
    decimal LedgerBalance,
    decimal ExpensePurchases,
    decimal SepaPaid,
    decimal UnclaimedPurchases,
    int MissingLines,
    int UnclaimedPurchaseCount,
    IReadOnlyList<ExpenseReconOpenTransfer> UnmatchedTransfers,
    IReadOnlyList<ExpenseReconMovement> OtherMovements,
    ExpenseReconMemberStatus Status,
    IReadOnlyList<ExpenseReconOpenTransfer> OpenTransfers)
{
    public decimal OtherTotal => OtherMovements.Sum(m => m.Amount);

    /// <summary>A warning, not a problem. An account-less member counts only when a line of theirs is Missing.</summary>
    public bool IsWarning => Status == ExpenseReconMemberStatus.Off
                             || (Status == ExpenseReconMemberStatus.NoAccount && MissingLines > 0);
}

/// <summary>A ledger line the expense check does not account for. <paramref name="Amount"/> is debit − credit.</summary>
public sealed record ExpenseReconMovement(
    LocalDate Date, int EntryNumber, string? Type, string? Description, decimal Amount);

public sealed record ExpenseReconOpenTransfer(Guid TransferId, string FileName, Instant GeneratedAt, decimal Amount);
