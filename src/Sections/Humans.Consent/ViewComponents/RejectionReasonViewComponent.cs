using Humans.Base.Enums;
using Humans.Consent.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Consent.ViewComponents;

/// <summary>The host renders the slot only for a Rejected account; this renders nothing outside Self view mode.</summary>
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
