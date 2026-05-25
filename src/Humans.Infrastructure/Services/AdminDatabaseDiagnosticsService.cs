using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using NodaTime;

namespace Humans.Infrastructure.Services;

public sealed class AdminDatabaseDiagnosticsService(
    IAdminDatabaseDiagnosticsRepository repository,
    IUserServiceRead userService,
    ITicketServiceRead ticketQueryService) : IAdminDatabaseDiagnosticsService
{
    public Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default) =>
        repository.GetMigrationStatusAsync(ct);

    public Task<int> ClearHangfireLocksAsync(CancellationToken ct = default) =>
        repository.ClearHangfireLocksAsync(ct);

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
