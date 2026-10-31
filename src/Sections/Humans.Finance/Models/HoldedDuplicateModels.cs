namespace Humans.Finance.Models;

/// <summary>Holded refuses to delete a document that carries a payment, and a draft reports no
/// <c>payments_pending</c> to judge by.</summary>
internal sealed record HoldedDuplicateRefusal(string DocId, decimal Total, string Reason);

/// <summary>
/// <paramref name="KeptDocId"/> is the one the line links to and is never deleted. <paramref name="DeletableDocIds"/>
/// are oldest id first; <paramref name="Refused"/> ones a human settles in Holded.
/// </summary>
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

/// <summary>
/// <paramref name="OrphanContactIds"/> are contacts the race created beside the bound one, holding only duplicates;
/// deleted once those documents are gone. Never the bound contact.
/// </summary>
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

/// <summary>Computed live from Holded's purchase list and Expenses' line bookings.</summary>
/// <remarks>
/// <paramref name="EmptyUnboundContacts"/> are creditor-range contacts bound to nobody with no documents. Holded has
/// no archive call, so they are listed for a human. When <paramref name="Error"/> is set the lists are empty.
/// </remarks>
internal sealed record HoldedDuplicatesVm(
    IReadOnlyList<HoldedDuplicateMember> Members,
    IReadOnlyList<(int AccountNum, string Summary)> EmptyUnboundContacts,
    string? Error)
{
    public int DocumentCount => Members.Sum(m => m.DeletableCount);

    public decimal Total => Members.Sum(m => m.DeletableTotal);

    public int ContactCount => Members.Sum(m => m.OrphanContactIds.Count);

    public int LineCount => Members.Sum(m => m.Lines.Count);

    public int RefusedCount => Members.Sum(m => m.RefusedCount);

    public bool HasWork => DocumentCount > 0 || ContactCount > 0;
}

/// <summary>The creditor balance before and after is read live, in Holded's own sign.</summary>
internal sealed record HoldedDuplicateMemberResult(
    Guid UserId,
    string ContactName,
    int? SupplierAccountNum,
    decimal? BalanceBefore,
    decimal? BalanceAfter,
    IReadOnlyList<string> DeletedDocIds,
    IReadOnlyList<string> DeletedContactIds,
    IReadOnlyList<string> Failures);

/// <summary>
/// <paramref name="Error"/>: nothing was deleted at all. <paramref name="SyncError"/>: the closing ledger sync failed,
/// but the deletions stand.
/// </summary>
internal sealed record HoldedDuplicateApplyResult(
    IReadOnlyList<HoldedDuplicateMemberResult> Members,
    string? Error,
    string? SyncError)
{
    public int DeletedDocuments => Members.Sum(m => m.DeletedDocIds.Count);

    public int DeletedContacts => Members.Sum(m => m.DeletedContactIds.Count);

    public int Failures => Members.Sum(m => m.Failures.Count);
}
