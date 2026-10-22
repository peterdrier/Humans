using System.Security.Claims;
using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing for a guest or non-member.</summary>
public sealed class GovernanceApplicationsTileViewComponent(
    IMembershipCalculator membershipCalculator) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var snapshot = await membershipCalculator.GetMembershipSnapshotAsync(userId, HttpContext.RequestAborted);
        return snapshot.IsVolunteerMember ? View() : Content(string.Empty);
    }
}
