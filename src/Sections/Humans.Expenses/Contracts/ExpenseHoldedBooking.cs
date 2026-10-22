namespace Humans.Expenses.Contracts;

/// <summary>
/// One expense line as pushed: description <c>ER: {line}</c>, notes <c>Expense report {report}</c>, and the payable
/// allocation (a trimmed line carries a negative adjustment). Legacy report-level documents are not included: they
/// predate per-line pushes and never raced. <see cref="CreditorUserId"/> is the merge survivor, who owns the binding.
/// </summary>
public sealed record ExpenseHoldedBooking(
    Guid ExpenseReportId,
    Guid LineId,
    Guid CreditorUserId,
    string? HoldedContactId,
    string HoldedDocId,
    string Description,
    string Notes,
    decimal BookedAmount);
