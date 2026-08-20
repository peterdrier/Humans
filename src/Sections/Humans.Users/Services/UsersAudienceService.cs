using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Services;

/// <summary>Backs <see cref="IUsersAudienceService"/> — see that interface for the contract.</summary>
internal sealed class UsersAudienceService(
    IUserService userService,
    ITicketServiceRead ticketQueryService) : IUsersAudienceService
{
    public async Task<AudienceSegmentation> GetAudienceSegmentationAsync(int? year, CancellationToken ct = default)
    {
        var allUsers = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var ticketOrders = await ticketQueryService.GetTicketOrdersAsync(ct);
        var start = year.HasValue ? Instant.FromUtc(year.Value, 1, 1, 0, 0) : default;
        var end = year.HasValue ? Instant.FromUtc(year.Value + 1, 1, 1, 0, 0) : default;
        IReadOnlySet<Guid> ticketUserIds = ticketOrders
            .Where(o => !year.HasValue || (o.PurchasedAt >= start && o.PurchasedAt < end))
            .SelectMany(o => o.MatchedUserId.HasValue
                ? o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value)
                    .Append(o.MatchedUserId.Value)
                : o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value))
            .ToHashSet();

        var withProfile = 0;
        var withTicket = 0;
        var withBoth = 0;
        var withNeither = 0;

        foreach (var user in allUsers)
        {
            var hasProfile = user.Profile is not null;
            var hasTicket = ticketUserIds.Contains(user.Id);

            if (hasProfile) withProfile++;
            if (hasTicket) withTicket++;
            if (hasProfile && hasTicket) withBoth++;
            if (!hasProfile && !hasTicket) withNeither++;
        }

        var years = ticketOrders
            .Where(o => o.MatchedUserId.HasValue)
            .Select(o => o.PurchasedAt.InUtc().Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        return new AudienceSegmentation(
            TotalAccounts: allUsers.Count,
            WithTicket: withTicket,
            WithProfile: withProfile,
            WithBoth: withBoth,
            WithNeither: withNeither,
            AvailableYears: years,
            SelectedYear: year);
    }
}
