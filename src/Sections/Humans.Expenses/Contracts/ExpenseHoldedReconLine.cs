using NodaTime;

namespace Humans.Expenses.Contracts;

/// <summary>
/// One expected purchase for ledger reconciliation: a bookable line after the cap allocation, pushed or not.
/// Description and booked amount match the creditor account’s purchase lines; CreditorUserId is the merge survivor.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><paramref name="LineId"/> is null for a legacy report-level document, which carries the whole payable and was
/// written before per-line descriptions; only its id identifies it.</item>
/// <item><paramref name="DocumentDate"/> is the date the push gives the document.</item>
/// <item><paramref name="SyncState"/> and <paramref name="SyncSettledAt"/> are the report's latest create event in
/// the Holded outbox.</item>
/// </list>
/// </remarks>
public sealed record ExpenseHoldedReconLine(
    Guid ExpenseReportId,
    Guid? LineId,
    ExpenseReportStatus ReportStatus,
    Guid CreditorUserId,
    string? HoldedDocId,
    string Description,
    decimal BookedAmount,
    Instant DocumentDate,
    ExpenseHoldedSyncState SyncState,
    Instant? SyncSettledAt);
