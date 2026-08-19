using System.Security.Claims;
using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>
/// The member dashboard's pending-consents nudge: a link to the agreement(s) still
/// awaiting the member's signature. Renders nothing when none are pending.
/// </summary>
public sealed class PendingConsentsAlertViewComponent(
    IMembershipCalculatorRead membershipCalculator) : ViewComponent
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
