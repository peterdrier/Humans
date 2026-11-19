using Humans.Base.Constants;
using Humans.Teams.Contracts;
using System.Security.Claims;
using Humans.Governance.Contracts;
using Humans.Governance.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing for a plain Volunteer.</summary>
public sealed class MemberTermStatusViewComponent(
    ITeamService teamService,
    IGovernanceService applicationService,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var tier = await applicationService.GetMembershipTierAsync(userId, ct);
        var isVolunteerMember = (await teamService.GetTeamAsync(SystemTeamIds.Volunteers, ct))
            is { IsActive: true } volunteers && volunteers.Members.Any(m => m.UserId == userId);

        if (!isVolunteerMember || tier == MembershipTier.Volunteer)
        {
            return Content(string.Empty);
        }

        var applications = await applicationService.GetUserApplicationsAsync(userId, ct);
        var (expiresAt, expiresSoon, expired, hasPendingApplication) = TermExpiryCalculator.ComputeTermState(
            applications, tier, clock.GetCurrentInstant().InUtc().Date);

        return View(new MemberTermStatusViewModel(
            tier,
            expiresAt?.AtMidnight().InUtc().ToDateTimeUtc(),
            expiresSoon,
            expired,
            hasPendingApplication));
    }


}

internal sealed record MemberTermStatusViewModel(
    MembershipTier MembershipTier,
    DateTime? TermExpiresAt,
    bool TermExpiresSoon,
    bool TermExpired,
    bool HasPendingApplication);
