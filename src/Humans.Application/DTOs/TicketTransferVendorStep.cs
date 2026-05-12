using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// Kinds of sub-step recorded inside <c>TicketTransferRequest.VendorStepsJson</c>.
/// </summary>
public enum TicketTransferVendorStepKind
{
    /// <summary>TT POST: void the original issued ticket (with hold).</summary>
    Void,

    /// <summary>TT POST: issue replacement against the reserved hold.</summary>
    Issue,

    /// <summary>Local DB upsert of the new + voided attendee rows.</summary>
    LocalWriteback,

    /// <summary>Retry of <see cref="Issue"/> by an admin after a partial failure.</summary>
    RetryIssue,

    /// <summary>Admin recorded that they reconciled the vendor side manually.</summary>
    ManualReconcile,
}

/// <summary>
/// One row in the structured vendor-step log. Captures what we asked the
/// vendor to do, what we got back, and what we then did locally. Append-only —
/// every change to the request appends a new step rather than mutating an
/// existing one. Surfaced via the transfer-review timeline.
/// </summary>
public sealed record TicketTransferVendorStep(
    TicketTransferVendorStepKind Kind,
    bool Success,
    Instant OccurredAt,
    string? VendorReferenceId,    // hold id on successful Void; new ticket id on successful Issue/RetryIssue
    string? RequestSummary,       // short; never the full request body
    string? ResponseSummary,      // short
    string? ErrorMessage);
