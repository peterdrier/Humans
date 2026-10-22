using Humans.Base.Enums;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Consent.ViewComponents;

/// <summary>Admin view mode only; the host page's own policy decides who reaches it.</summary>
public sealed class ConsentReviewCardViewComponent(
    IConsentService consent,
    IUserService users) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin)
            return Content(string.Empty);

        var ct = HttpContext.RequestAborted;
        var info = await users.GetRawUserInfoAsync(userId, ct);
        if (info is null)
            return Content(string.Empty);

        var review = await consent.GetConsentReviewAsync(userId, ct);
        var rejectedByName = review.RejectedByUserId is { } rejectedBy
            ? (await users.GetRawUserInfoAsync(rejectedBy, ct))?.BurnerName
            : null;

        return View(new ConsentReviewCardViewModel(
            info.State == UserState.Rejected,
            review.Status,
            review.RejectionReason,
            review.RejectedAt?.ToDateTimeUtc(),
            rejectedByName));
    }
}

internal sealed record ConsentReviewCardViewModel(
    bool IsRejected,
    ConsentCheckStatus? Status,
    string? RejectionReason,
    DateTime? RejectedAt,
    string? RejectedByName);
