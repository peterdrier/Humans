using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Teams.Services;

/// <summary>
/// Admin's pending join requests across all teams; coordinators see their own team's on the team page. Read from
/// the in-RAM team snapshot, which Teams drops on its own join-request writes.
/// </summary>
internal sealed class TeamsNotificationMeters(
    ITeamService teamService,
    IStringLocalizer<TeamsResource> localizer) : INotificationMeterContributor
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default)
    {
        if (!viewer.IsInRole(RoleNames.Admin))
            return [];

        var count = (await teamService.GetTeamsAsync(ct)).Values.Sum(t => t.PendingRequestCount);
        if (count == 0)
            return [];

        return
        [
            new NotificationMeter
            {
                Title = localizer["Teams_Meter_JoinRequestsPending"],
                Count = count,
                ActionUrl = "/Teams/Summary",
                Audience = localizer["Teams_Meter_AudienceAdmin"],
                Priority = 5,
            },
        ];
    }
}
