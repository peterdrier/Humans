using NodaTime;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services.Dtos;

/// <summary>
/// Sync joins on <c>VendorTicketId</c>, not email: <c>MatchedUserId</c> can diverge from the email after a merge.
/// Read <c>CheckedInAt</c> for check-in; <c>Status</c> stays Valid once scanned.
/// </summary>
internal sealed record MatchedAttendeeRow(
    string VendorTicketId, Guid MatchedUserId, TicketAttendeeStatus Status, Instant? CheckedInAt);

internal sealed record OrderDiscountCodeRow(string Code, Instant PurchasedAt);
