using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>Single match returned by Receiver lookup. Empty list means "no match".</summary>
public sealed record ReceiverLookupResultDto(
    Guid UserId,
    string DisplayName,
    string? BurnerName,
    string? PreferredEmail,
    bool HasCustomProfilePicture,
    string? ProfilePictureUrl);

/// <summary>Submitted by the Sender's Receiver-lookup form.</summary>
public sealed record ReceiverLookupRequest(string Query);

/// <summary>Submitted by the Sender when confirming the Receiver.</summary>
public sealed record TicketTransferRequestDto(
    Guid OriginalAttendeeId,
    Guid ReceiverUserId,
    string Reason);

/// <summary>Admin decision payload.</summary>
public sealed record TicketTransferDecisionDto(
    Guid TransferRequestId,
    bool Approve,
    string? AdminNotes);

/// <summary>Read-side DTO for the admin queue.</summary>
public sealed record TicketTransferRowDto(
    Guid Id,
    Guid OriginalAttendeeId,
    string OriginalAttendeeName,
    string TicketTypeName,
    TicketAttendeeStatus OriginalAttendeeStatus,
    Guid SenderUserId,
    string SenderDisplayName,
    Guid ReceiverUserId,
    string ReceiverLegalName,
    string ReceiverEmail,
    string SenderReason,
    TicketTransferStatus Status,
    TicketTransferVendorResult VendorResult,
    string? VendorMessage,
    Guid? DecidedByUserId,
    string? DecidedByDisplayName,
    string? AdminNotes,
    Instant RequestedAt,
    Instant? DecidedAt);

/// <summary>
/// Read-side DTO for the admin Detail review screen — adds profile cards for
/// both sides of the transfer so the admin can sanity-check identities and
/// catch anything nefarious before approving.
/// </summary>
public sealed record TicketTransferDetailDto(
    TicketTransferRowDto Row,
    ReceiverLookupResultDto SenderCard,
    ReceiverLookupResultDto ReceiverCard,
    string? OrderDashboardUrl,
    string VendorStepsJson);

/// <summary>
/// One row in the homepage "My tickets" attendee list, with eligibility flags
/// for sending and any pending outgoing transfer pre-computed in the service.
/// </summary>
public sealed record MyAttendeeRowDto(
    Guid AttendeeId,
    string AttendeeName,
    string TicketTypeName,
    bool CanSendTransfer,
    bool HasPendingOutgoingTransfer,
    Guid? PendingTransferRequestId);

/// <summary>Outcome of a TT void call.</summary>
public sealed record VoidIssuedTicketResult(string VendorTicketId, string? HoldId);

/// <summary>Payload for TT issue-ticket call. EventId+TicketTypeId XOR HoldId is required.</summary>
public sealed record IssueTicketRequest(
    string? EventId,
    string? TicketTypeId,
    string? HoldId,
    string FullName,
    string? Email,
    bool SendEmail,
    string? ExternalReference);

/// <summary>Categorised vendor failure for Option-C fallback decisions in the service.</summary>
public sealed class TicketVendorWriteException : Exception
{
    public TicketVendorWriteException() { }
    public TicketVendorWriteException(string message) : base(message) { }
    public TicketVendorWriteException(string message, Exception inner) : base(message, inner) { }

    public TicketVendorWriteException(string message, TicketVendorFailureKind kind, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
    public TicketVendorFailureKind Kind { get; }
}

public enum TicketVendorFailureKind
{
    /// <summary>HTTP 400 / 422 — bad payload, sold out, seated ticket type. Do not retry.</summary>
    Validation,
    /// <summary>HTTP 401/403 — credential rotation problem. Do not retry.</summary>
    AuthFailed,
    /// <summary>HTTP 404 — ticket already voided or unknown. Treat per-call.</summary>
    NotFound,
    /// <summary>HTTP 429 — rate limited. Surface to user; do not auto-retry mid-request.</summary>
    RateLimited,
    /// <summary>HTTP 5xx or transport failure. May retry from admin UI.</summary>
    Transient,
}
