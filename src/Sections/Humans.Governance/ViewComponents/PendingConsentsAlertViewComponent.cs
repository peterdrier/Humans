using Humans.Base.Extensions;
using Humans.Consent.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing when no consents are pending.</summary>
public sealed class PendingConsentsAlertViewComponent(
    IConsentService consentService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal.GetUserId() is not { } userId)
        {
            return Content(string.Empty);
        }

        var progress = await consentService.GetConsentProgressAsync(userId, HttpContext.RequestAborted);
        return progress.PendingCount > 0 ? View(progress.PendingCount) : Content(string.Empty);
    }
}
