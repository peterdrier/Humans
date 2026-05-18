using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// A single user email entry, shaped for building the
/// email → userId lookup that <see cref="Humans.Application.Services.Tickets.TicketSyncService"/>
/// uses to match vendor buyer/attendee emails to users.
/// Read-only projection owned by <c>ITicketRepository</c>.
/// Only verified emails are included — unverified emails are not trustworthy
/// enough to drive ticket-to-user matching (issue nobodies-collective/Humans#645).
/// </summary>
public record UserEmailLookupEntry(string Email, Guid UserId);

/// <summary>
/// An attendee row projected for the event-participation reconciliation pass.
/// Only includes attendees with a non-null <c>MatchedUserId</c>, scoped to a
/// single vendor event (matching <see cref="TicketSyncService"/> behavior).
/// <c>VendorTicketId</c> is included so sync can join the row to the vendor
/// delta by ticket identity, not by attendee email — necessary because
/// <c>MatchedUserId</c> can diverge from the email path after account-merge
/// re-FK or non-email match assignment.
/// </summary>
public record MatchedAttendeeRow(string VendorTicketId, Guid MatchedUserId, TicketAttendeeStatus Status);

/// <summary>
/// A discount-code projection row used to build
/// <see cref="DiscountCodeRedemption"/> entries that feed
/// <c>ICampaignService.MarkGrantsRedeemedAsync</c>.
/// </summary>
public record OrderDiscountCodeRow(string Code, Instant PurchasedAt);
