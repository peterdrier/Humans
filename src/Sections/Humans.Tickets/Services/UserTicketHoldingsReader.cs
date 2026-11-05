using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed class UserTicketHoldingsReader(
    ITicketRepository ticketRepository,
    ITicketTransferRepository ticketTransferRepository,
    IUserService userService,
    ISettingsService settingsService,
    IClock clock) : IApplicationService
{
    internal async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var orders = await ticketRepository.GetOrdersMatchedToUserAsync(userId, ct);
        var orderCount = orders.Count;
        var orderSummaries = orders.Select(o => new UserTicketOrderSummary(
            o.BuyerName,
            o.PurchasedAt,
            o.Attendees.Count,
            o.TotalAmount,
            o.Currency)).ToList();
        var openTicketOrderIds = orders
            .Where(o => o.PaymentStatus is TicketPaymentStatus.Paid or TicketPaymentStatus.Pending)
            .Select(o => o.Id)
            .ToList();

        var attendees = await ticketRepository.GetAttendeesVisibleToUserAsync(userId, ct);
        // GroupBy, not ToDictionary: stray duplicate pendings must not crash the dashboard read.
        var pendingByAttendee = (await ticketTransferRepository.GetBySenderAsync(userId, ct))
            .Where(r => r.Status == TicketTransferStatus.Pending)
            .GroupBy(r => r.OriginalTicketAttendeeId)
            .ToDictionary(g => g.Key, g => g.First().Id);
        var tickets = attendees
            .Where(a => TicketAttendeeOwnership.IsCurrentOwner(a, userId))
            .Select(a => new UserTicketHoldingRow(
                a.Id,
                a.AttendeeName,
                a.AttendeeEmail,
                a.VendorTicketId,
                a.TicketTypeName,
                a.Status,
                HasPendingOutgoingTransfer: pendingByAttendee.ContainsKey(a.Id),
                PendingTransferRequestId: pendingByAttendee.TryGetValue(a.Id, out var transferId)
                    ? transferId
                    : null,
                CheckedInAt: a.CheckedInAt))
            .ToList();

        var ticketCount = await ComputeUserTicketCountAsync(userId, ct);
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        var hasCurrentEventTicket = syncState is not null
            && !string.IsNullOrEmpty(syncState.VendorEventId)
            && await ticketRepository.HasEventTicketAsync(userId, syncState.VendorEventId, ct);
        var postEventHoldDate = hasCurrentEventTicket
            ? await GetPostEventHoldDateAsync(ct)
            : null;

        return new UserTicketHoldings(
            orderCount,
            tickets,
            hasCurrentEventTicket,
            ticketCount,
            postEventHoldDate)
        {
            OrderSummaries = orderSummaries,
            OpenTicketOrderIds = openTicketOrderIds,
        };
    }

    private async Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        if (activeEvent is null)
            return null;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId)
                 ?? DateTimeZone.Utc;
        var postEventDate = activeEvent.GateOpeningDate
            .PlusDays(activeEvent.StrikeEndOffset + 1);
        var postEventInstant = postEventDate
            .AtStartOfDayInZone(tz)
            .ToInstant();

        var now = clock.GetCurrentInstant();
        return postEventInstant > now ? postEventInstant : null;
    }

    private async Task<int> ComputeUserTicketCountAsync(Guid userId, CancellationToken ct)
    {
        // Attendees only: buyers don't count for themselves.
        var matchedCount = await ticketRepository
            .CountValidAttendeesMatchedToUserAsync(userId, ct);
        if (matchedCount > 0)
            return matchedCount;

        // Same verified-email index the sync matches with, so an alias verified by two users counts for neither.
        var attendeeEmails = await ticketRepository.GetValidAttendeeEmailsAsync(ct);
        if (attendeeEmails.Count == 0)
            return 0;

        var (lookup, _) = VerifiedEmailLookup.Build(await userService.GetAllUserInfosAsync(ct));
        return attendeeEmails.Count(email => lookup.TryGetValue(email, out var owner) && owner == userId);
    }

}
