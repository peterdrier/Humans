using System.Security.Claims;
using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing when no consents are pending.</summary>
public sealed class PendingConsentsAlertViewComponent(
    IMembershipCalculator membershipCalculator) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var snapshot = await membershipCalculator.GetMembershipSnapshotAsync(userId, HttpContext.RequestAborted);
        return snapshot.PendingConsentCount > 0 ? View(snapshot.PendingConsentCount) : Content(string.Empty);
    }
}
