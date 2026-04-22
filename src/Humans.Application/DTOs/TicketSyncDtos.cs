using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// A single user email entry, shaped for building the
/// email → userId lookup that <see cref="Humans.Application.Services.Tickets.TicketSyncService"/>
/// uses to match vendor buyer/attendee emails to users.
/// Read-only projection owned by <c>ITicketRepository</c>.
/// </summary>
public record UserEmailLookupEntry(string Email, Guid UserId, bool IsOAuth);

/// <summary>
/// An attendee row projected for the event-participation reconciliation pass.
/// Only includes attendees with a non-null <c>MatchedUserId</c>, scoped to a
/// single vendor event (matching <see cref="TicketSyncService"/> behavior).
/// </summary>
public record MatchedAttendeeRow(Guid MatchedUserId, TicketAttendeeStatus Status);

/// <summary>
/// A discount-code projection row used to build
/// <see cref="DiscountCodeRedemption"/> entries that feed
/// <c>ICampaignService.MarkGrantsRedeemedAsync</c>.
/// </summary>
public record OrderDiscountCodeRow(string Code, Instant PurchasedAt);
