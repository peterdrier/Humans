using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Meter: number of pending team join requests across all teams. Registered by the
/// Teams section (which owns the <c>team_join_requests</c> table) per the push-model
/// design in issue nobodies-collective/Humans#581.
/// </summary>
/// <remarks>
/// Coordinators see per-team join requests on the team page directly; this admin-wide
/// meter intentionally shows only to global Admins.
/// </remarks>
public sealed class TeamJoinRequestsPendingMeterContributor : INotificationMeterContributor
{
    private readonly ITeamService _teamService;

    public TeamJoinRequestsPendingMeterContributor(ITeamService teamService)
    {
        _teamService = teamService;
    }

    public string Key => "TeamJoinRequestsPending";

    public NotificationMeterScope Scope => NotificationMeterScope.Global;

    public bool IsVisibleTo(ClaimsPrincipal user) => user.IsInRole(RoleNames.Admin);

    public async Task<NotificationMeter?> BuildMeterAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var count = await _teamService.GetTotalPendingJoinRequestCountAsync(cancellationToken);
        if (count <= 0) return null;

        return new NotificationMeter
        {
            Title = "Team join requests pending",
            Count = count,
            ActionUrl = "/Teams/Summary",
            Priority = 5,
        };
    }
}
