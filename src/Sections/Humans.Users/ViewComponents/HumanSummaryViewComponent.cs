using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Models;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Users.ViewComponents;

/// <summary>
/// Renders the same content as the hover popover (<c>_HumanPopover.cshtml</c>)
/// inline — avatar, name, membership badge, city/country, public teams +
/// (admin-only) hidden teams, and languages. Use on admin-review screens
/// (e.g. ticket-transfer detail) where the reviewer needs identity context
/// without having to hover.
/// </summary>
public sealed class HumanSummaryViewComponent(
    IUserService userService, ITeamService teamService, IGovernanceService governanceService,
    IConsentService consentService) : ViewComponent
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
                DisplayName = "(unknown)",
                HasProfile = false,
            });
        }

        if (info.Profile is null)
        {
            return View("Default", new ProfileSummaryViewModel
            {
                UserId = userId,
                DisplayName = info.BurnerName,
                Email = info.PrimaryEmail
                    ?? info.UserEmails.FirstOrDefault(e => e.IsVerified)?.Email,
                ProfilePictureUrl = info.ProfilePictureUrl,
                PreferredLanguage = info.PreferredLanguage,
                HasProfile = false,
            });
        }

        var memberships = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive && t.SystemTeamType != SystemTeamType.Volunteers)
            .Select(t => new { TeamInfo = t, Membership = t.Members.FirstOrDefault(m => m.UserId == userId) })
            .Where(x => x.Membership is not null)
            .Select(x => new TeamMembership(x.TeamInfo.Name, x.Membership!.Role) { IsHidden = x.TeamInfo.IsHidden })
            .ToList();

        var tier = await governanceService.GetMembershipTierAsync(userId, ct);
        var isApproved = (await consentService.GetConsentReviewAsync(userId, ct)).IsApproved;

        var vm = ProfileSummaryViewModelBuilder.BuildWithProfile(info, memberships, tier, isApproved);

        return View("Default", vm);
    }
}
