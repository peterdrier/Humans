namespace Humans.Expenses.Contracts;

/// <summary>
/// One expense line as it was pushed to Holded: the purchase document the line links to and the
/// values the push wrote on it (the publisher sets description
/// <c>ER: {line}</c>, notes <c>Expense report {report}</c> and books the payable allocation, a
/// trimmed line carrying a negative adjustment line so the document total is the booked amount).
/// Finance matches Holded's purchase list against these to find documents the push created twice.
/// Legacy report-level documents (<see cref="ExpenseReportDto.HoldedDocId"/>) are not included:
/// they predate per-line pushes and never raced.
/// </summary>
public sealed record ExpenseHoldedBooking(
    Guid ExpenseReportId,
    Guid LineId,
    Guid SubmitterUserId,
    string? HoldedContactId,
    string HoldedDocId,
    string Description,
    string Notes,
    decimal BookedAmount);
