using NodaTime;

namespace Humans.Expenses.Contracts;

/// <summary>
/// One Holded purchase document Humans expects to exist: a bookable line after the cap allocation, pushed or not.
/// <see cref="Description"/>, <see cref="Notes"/> and <see cref="BookedAmount"/> are what the push writes, so they
/// compare directly with the document. <see cref="CreditorUserId"/> is the merge survivor, who owns the binding.
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
    string? HoldedContactId,
    string? HoldedDocId,
    string Description,
    string Notes,
    decimal BookedAmount,
    Instant DocumentDate,
    ExpenseHoldedSyncState SyncState,
    Instant? SyncSettledAt);
