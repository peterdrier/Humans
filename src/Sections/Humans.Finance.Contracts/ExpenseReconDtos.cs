using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>
/// What Humans approved against what the local Holded mirrors hold: expense lines, stray documents carrying the push's
/// marker, and each member's creditor balance. Built from local data only; <see cref="Year"/> narrows lines and
/// orphans, never members, whose ledger balances are all-time.
/// </summary>
/// <remarks><paramref name="UnavailableReason"/> is set, and the lists are empty, when the document mirror has never
/// synced, so nothing can be judged.</remarks>
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

/// <summary><see cref="Problems"/> is what the "0 problems" line counts: problem lines, orphans and members
/// whose balance is off.</summary>
public sealed record ExpenseReconTotals(
    int Reports,
    int Lines,
    int HoldedDocs,
    int ProblemLines,
    int DuplicateDocs,
    decimal ExcessAmount,
    int Orphans,
    int MembersOff)
{
    public int Problems => ProblemLines + Orphans + MembersOff;
}

public enum ExpenseReconLineStatus
{
    /// <summary>Exactly one document, at the booked amount, on the member's bound contact.</summary>
    Booked,
    /// <summary>Not in the mirror yet and nothing has gone wrong: the push is queued or retrying, or it landed after
    /// the mirror's last sync. Not a problem.</summary>
    Pending,
    /// <summary>Approved, but no document in Holded: see <see cref="ExpenseReconLine.PushState"/>.</summary>
    Missing,
    /// <summary>More than one document matches the line.</summary>
    Duplicated,
    /// <summary>The line's document totals something other than the booked amount.</summary>
    AmountMismatch,
    /// <summary>The line's document is on a contact other than the member's bound one: a twin, or nobody's.</summary>
    WrongContact,
}

/// <summary>
/// One expected document. <paramref name="LineId"/> is null for a legacy report-level document.
/// <paramref name="PushState"/> is the report's Holded outbox state (<c>Queued</c>, <c>Retrying</c>, <c>Failed</c>,
/// <c>Pushed</c>, <c>NotQueued</c>, <c>NotConfigured</c>). The <c>Holded*</c> fields describe the line's own document
/// and are null when the mirror lacks it; <paramref name="ExcessAmount"/> is the sum of the extra documents.
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
    string? HoldedDocNumber,
    decimal? HoldedTotal,
    string? HoldedContactName,
    IReadOnlyList<string> DuplicateDocIds,
    decimal ExcessAmount)
{
    public bool IsProblem => Status is not (ExpenseReconLineStatus.Booked or ExpenseReconLineStatus.Pending);
}

public enum ExpenseReconOrphanKind
{
    /// <summary>On a bound member's contact, but no expense line claims it.</summary>
    NoMatchingLine,
    /// <summary>On a contact no member is bound to.</summary>
    UnboundContact,
}

/// <summary>A mirrored document carrying the push's marker (notes <c>Expense report …</c> or description
/// <c>ER: …</c>) that no line keeps and none counts as its duplicate.</summary>
public sealed record ExpenseReconOrphan(
    string HoldedDocId,
    string DocNumber,
    LocalDate Date,
    string ContactName,
    string? Description,
    string? Notes,
    decimal Total,
    ExpenseReconOrphanKind Kind);

public enum ExpenseReconMemberStatus
{
    Balanced,
    Off,
    /// <summary>No creditor binding with an account number, so there is no ledger to compare.</summary>
    NoAccount,
}

/// <summary>
/// Balances use Holded's sign, Σdebit − Σcredit: negative is owed to the member. <paramref name="ExpectedBalance"/> is
/// minus the expected amount of every line whose document is in the mirror, plus every booked SEPA transfer to the
/// account. <paramref name="OpenTransfers"/> are generated and neither booked nor withdrawn: money that may have left
/// the bank but is not in the ledger yet.
/// </summary>
public sealed record ExpenseReconMember(
    Guid UserId,
    string MemberName,
    int? SupplierAccountNum,
    decimal LedgerBalance,
    decimal ExpectedBalance,
    ExpenseReconMemberStatus Status,
    IReadOnlyList<ExpenseReconOpenTransfer> OpenTransfers)
{
    public decimal Difference => LedgerBalance - ExpectedBalance;

    /// <summary>An account-less member counts only when Holded should already hold something of theirs.</summary>
    public bool IsProblem => Status == ExpenseReconMemberStatus.Off
                             || (Status == ExpenseReconMemberStatus.NoAccount && ExpectedBalance != 0m);
}

public sealed record ExpenseReconOpenTransfer(Guid TransferId, string FileName, Instant GeneratedAt, decimal Amount);
