using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>Single match returned by recipient lookup. Null result means "no match".</summary>
public sealed record RecipientLookupResultDto(
    Guid UserId,
    string DisplayName,
    string? BurnerName,
    string? PreferredEmail,
    bool HasCustomProfilePicture,
    string? ProfilePictureUrl);

/// <summary>Submitted by the buyer's recipient-lookup form.</summary>
public sealed record RecipientLookupRequest(string Query);

/// <summary>Submitted by the buyer when confirming the recipient.</summary>
public sealed record TicketTransferRequestDto(
    Guid OriginalAttendeeId,
    Guid RecipientUserId,
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
    Guid RequesterUserId,
    string RequesterDisplayName,
    Guid RecipientUserId,
    string RecipientDisplayName,
    string RecipientEmail,
    string RequesterReason,
    TicketTransferStatus Status,
    TicketTransferVendorResult VendorResult,
    string? VendorMessage,
    Guid? DecidedByUserId,
    string? DecidedByDisplayName,
    string? AdminNotes,
    Instant RequestedAt,
    Instant? DecidedAt);
