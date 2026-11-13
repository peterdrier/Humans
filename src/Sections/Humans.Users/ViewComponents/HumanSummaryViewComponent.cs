using Humans.Governance.Contracts;
using Humans.Users.Models;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The hover popover's content, inline, for admin-review screens where the reviewer needs
/// identity context without hovering.
/// </summary>
public sealed class HumanSummaryViewComponent(
    IUserService userService, ITeamService teamService, IGovernanceService governanceService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is not null)
            userId = info.Id; // team memberships below are keyed by the live id
        if (info is null)
        {
            return View("Default", new ProfileSummaryViewModel
            {
                UserId = userId,
                HasProfile = false,
            });
        }

        if (info.Profile is null)
        {
            return View("Default", new ProfileSummaryViewModel
            {
                UserId = userId,
                Email = info.Email,
                PreferredLanguage = info.PreferredLanguage,
                HasProfile = false,
            });
        }

        var memberships = (await teamService.GetUserTeamMembershipsAsync(userId, ct))
            .Where(m => m.IsProfileMembership)
            .Select(m => new TeamMembership(m.TeamName, m.Role) { IsHidden = m.IsHidden })
            .ToList();

        var tier = await governanceService.GetMembershipTierAsync(userId, ct);

        var vm = ProfileSummaryViewModelBuilder.BuildWithProfile(info, memberships, tier);

        return View("Default", vm);
    }
}
