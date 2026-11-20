using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Users.Services;

/// <summary>Admin's pending account deletions, counted from the in-RAM user snapshot.</summary>
internal sealed class UsersNotificationMeters(
    IUserService userService,
    IStringLocalizer<UsersResource> localizer) : INotificationMeterContributor
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default)
    {
        if (!viewer.IsInRole(RoleNames.Admin))
            return [];

        var count = (await userService.GetAllUserInfosAsync(ct)).Count(u => u.DeletionRequestedAt != null);
        if (count == 0)
            return [];

        return
        [
            new NotificationMeter
            {
                Title = localizer["Users_Meter_PendingAccountDeletions"],
                Count = count,
                ActionUrl = "/Users/Admin?filter=deleting&sort=name&dir=asc",
                Audience = localizer["Users_Meter_AudienceAdmin"],
                Priority = 8,
            },
        ];
    }
}
