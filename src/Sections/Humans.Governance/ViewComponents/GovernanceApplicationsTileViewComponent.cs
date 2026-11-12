using Humans.Base.Constants;
using Humans.Teams.Contracts;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing for a guest or non-member.</summary>
public sealed class GovernanceApplicationsTileViewComponent(
    ITeamService teamService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var isVolunteerMember = (await teamService.GetTeamAsync(SystemTeamIds.Volunteers, HttpContext.RequestAborted))
            is { IsActive: true } volunteers && volunteers.Members.Any(m => m.UserId == userId);
        return isVolunteerMember ? View() : Content(string.Empty);
    }
}
