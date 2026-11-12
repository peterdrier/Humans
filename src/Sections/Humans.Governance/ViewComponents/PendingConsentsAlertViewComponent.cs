using Humans.Consent.Contracts;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing when no consents are pending.</summary>
public sealed class PendingConsentsAlertViewComponent(
    IConsentService consentService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var progress = await consentService.GetConsentProgressAsync(userId, HttpContext.RequestAborted);
        return progress.PendingCount > 0 ? View(progress.PendingCount) : Content(string.Empty);
    }
}
