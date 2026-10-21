using Humans.Base.Enums;
using Humans.Consent.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Consent.ViewComponents;

/// <summary>
/// The rejection reason on the member's own account-status wall, contributed to
/// <c>UserPartSlots.AccountStatus</c> (the host renders the slot for a Rejected account only).
/// Renders nothing outside the Self view mode or when no reason was recorded.
/// </summary>
public sealed class RejectionReasonViewComponent(IConsentService consent) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Self)
            return Content(string.Empty);

        var review = await consent.GetConsentReviewAsync(userId, HttpContext.RequestAborted);
        return string.IsNullOrWhiteSpace(review.RejectionReason)
            ? Content(string.Empty)
            : View(model: review.RejectionReason);
    }
}
