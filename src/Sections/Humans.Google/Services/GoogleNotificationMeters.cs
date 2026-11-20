using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Google.Contracts;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Google.Services;

/// <summary>Admin's failed sync events: one count query, asked only for Admins.</summary>
internal sealed class GoogleNotificationMeters(
    IGoogleSyncService googleSyncService,
    IStringLocalizer<GoogleResource> localizer) : INotificationMeterContributor
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default)
    {
        if (!viewer.IsInRole(RoleNames.Admin))
            return [];

        var count = await googleSyncService.GetFailedSyncEventCountAsync(ct);
        if (count == 0)
            return [];

        return
        [
            new NotificationMeter
            {
                Title = localizer["Google_Meter_FailedSyncEvents"],
                Count = count,
                ActionUrl = "/Google/SyncOutbox",
                Audience = localizer["Google_Meter_AudienceAdmin"],
                Priority = 7,
            },
        ];
    }
}
