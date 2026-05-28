using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs;

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
