namespace Humans.Finance.Models;

/// <summary>One Holded document the page would not delete, and why: Holded refuses to delete a
/// document that carries a payment, and a draft reports no <c>payments_pending</c> to judge by.</summary>
internal sealed record HoldedDuplicateRefusal(string DocId, decimal Total, string Reason);

/// <summary>One expense line and the documents that duplicate it.</summary>
/// <param name="ExpenseReportId">The report the line belongs to.</param>
/// <param name="LineId">The expense line.</param>
/// <param name="Description">The line's description, as the document carries it.</param>
/// <param name="Amount">What the line booked — every duplicate's total.</param>
/// <param name="KeptDocId">The document the line links to; never deleted.</param>
/// <param name="DeletableDocIds">Duplicates Apply will delete, oldest id first.</param>
/// <param name="Refused">Duplicates a human has to settle in Holded.</param>
internal sealed record HoldedDuplicateLine(
    Guid ExpenseReportId,
    Guid LineId,
    string Description,
    decimal Amount,
    string KeptDocId,
    IReadOnlyList<string> DeletableDocIds,
    IReadOnlyList<HoldedDuplicateRefusal> Refused)
{
    public decimal DeletableTotal => DeletableDocIds.Count * Amount;
}

/// <summary>One member's share of the plan.</summary>
/// <param name="UserId">The member.</param>
/// <param name="BoundContactId">The Holded contact the member's creditor binding names, if bound.</param>
/// <param name="SupplierAccountNum">The member's creditor account.</param>
/// <param name="Lines">The member's lines with duplicates.</param>
/// <param name="ContactName">The Holded name of the contact the member is bound to (or the one
/// their documents sit on), for the screen and the audit lines.</param>
/// <param name="OrphanContactIds">Contacts the race created beside the bound one, every document of
/// which is a duplicate above: deleted once those documents are gone. Never the bound contact.</param>
internal sealed record HoldedDuplicateMember(
    Guid UserId,
    string ContactName,
    string? BoundContactId,
    int? SupplierAccountNum,
    IReadOnlyList<HoldedDuplicateLine> Lines,
    IReadOnlyList<string> OrphanContactIds)
{
    public int DeletableCount => Lines.Sum(l => l.DeletableDocIds.Count);

    public decimal DeletableTotal => Lines.Sum(l => l.DeletableTotal);

    public int RefusedCount => Lines.Sum(l => l.Refused.Count);
}

/// <summary>The /Finance/HoldedDuplicates dry run: what Apply would delete, computed live from
/// Holded's purchase list and Expenses' line bookings.</summary>
/// <param name="Members">Every member with at least one duplicated line.</param>
/// <param name="EmptyUnboundContactIds">Creditor-range Holded contacts bound to nobody with no
/// purchase document at all (the race also left one of these). Holded has no archive call, so they
/// are listed for a human rather than touched.</param>
/// <param name="Error">Why the plan could not be computed (Holded unreachable); the lists are then empty.</param>
internal sealed record HoldedDuplicatesVm(
    IReadOnlyList<HoldedDuplicateMember> Members,
    IReadOnlyList<string> EmptyUnboundContactIds,
    string? Error)
{
    public int DocumentCount => Members.Sum(m => m.DeletableCount);

    public decimal Total => Members.Sum(m => m.DeletableTotal);

    public int ContactCount => Members.Sum(m => m.OrphanContactIds.Count);

    public int LineCount => Members.Sum(m => m.Lines.Count);

    public int RefusedCount => Members.Sum(m => m.RefusedCount);

    public bool HasWork => DocumentCount > 0 || ContactCount > 0;
}

/// <summary>What Apply did for one member: the creditor balance before and after (live reads,
/// Holded's own sign), what was deleted and what Holded refused.</summary>
internal sealed record HoldedDuplicateMemberResult(
    Guid UserId,
    string ContactName,
    int? SupplierAccountNum,
    decimal? BalanceBefore,
    decimal? BalanceAfter,
    IReadOnlyList<string> DeletedDocIds,
    IReadOnlyList<string> DeletedContactIds,
    IReadOnlyList<string> Failures);

/// <summary>The outcome of one Apply.</summary>
/// <param name="Members">What was done per member.</param>
/// <param name="Error">Why nothing was deleted at all.</param>
/// <param name="SyncError">Set when the closing ledger sync failed; the deletions still stand.</param>
internal sealed record HoldedDuplicateApplyResult(
    IReadOnlyList<HoldedDuplicateMemberResult> Members,
    string? Error,
    string? SyncError)
{
    public int DeletedDocuments => Members.Sum(m => m.DeletedDocIds.Count);

    public int DeletedContacts => Members.Sum(m => m.DeletedContactIds.Count);

    public int Failures => Members.Sum(m => m.Failures.Count);
}
