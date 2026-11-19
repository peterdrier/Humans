using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Governance.Services;

/// <summary>The Board's "applications pending your vote" meter.</summary>
internal sealed class GovernanceNotificationMeters(
    IGovernanceManagement applications,
    IStringLocalizer<GovernanceResource> localizer) : INotificationMeterContributor
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default)
    {
        if (!viewer.IsInRole(RoleNames.Board))
            return [];

        var count = await applications.GetUnvotedApplicationCountAsync(viewerId, ct);
        if (count == 0)
            return [];

        return
        [
            new NotificationMeter
            {
                Title = localizer["Governance_Meter_ApplicationsPendingVote"],
                Count = count,
                ActionUrl = "/Governance/BoardVoting",
                Audience = localizer["Governance_Meter_AudienceBoard"],
                Priority = 9,
            },
        ];
    }
}
