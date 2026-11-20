using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Notifications.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Tickets.Services;

/// <summary>Admin's ticket-sync error flag: one sync-state row, asked only for Admins.</summary>
internal sealed class TicketsNotificationMeters(
    ITicketSync ticketSync,
    IStringLocalizer<TicketsResource> localizer) : INotificationMeterContributor
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default)
    {
        if (!viewer.IsInRole(RoleNames.Admin) || !await ticketSync.IsInErrorStateAsync(ct))
            return [];

        return
        [
            new NotificationMeter
            {
                Title = localizer["Tickets_Meter_SyncError"],
                Count = 1,
                ActionUrl = "/Tickets",
                Audience = localizer["Tickets_Meter_AudienceAdmin"],
                Priority = 4,
            },
        ];
    }
}
