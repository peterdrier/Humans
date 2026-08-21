namespace Humans.Expenses.Domain;

/// <summary>
/// Lifecycle of a vendor commitment (nobodies-collective/Humans#1030):
/// <c>Open → PartiallyPaid/Paid → Invoiced → Closed</c>. Payment states are derived from the
/// recorded payments; <c>Invoiced</c> means a Holded purchase document is linked.
/// </summary>
internal enum VendorCommitmentStatus
{
    Open,
    PartiallyPaid,
    Paid,
    Invoiced,
    Closed
}
