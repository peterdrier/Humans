using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Grants.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Grants.ViewComponents;

/// <summary>
/// The "Grants" card, contributed to <c>UserPartSlots.AdminDetail</c>. Admin audience only:
/// renders for <see cref="ProfileCardViewMode.Admin"/> when the viewer passes the same
/// <see cref="PolicyNames.HumanAdminBoardOrAdmin"/> policy as the host page, and nothing for
/// anyone else. Shows every grant of the person, whatever its state.
/// </summary>
internal sealed class UserGrantsViewComponent(
    IGrantManagement grants,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded)
            return Content(string.Empty);

        var all = await grants.GetGrantsForUserAsync(userId, HttpContext.RequestAborted);
        if (all.Count == 0) return Content(string.Empty);

        return View(new GrantsCardViewModel(all.OrderByDescending(g => g.IssuedAt).ToList(), grants.Today));
    }
}

/// <summary>The grants a card shows and the date their state is read against.</summary>
internal sealed record GrantsCardViewModel(IReadOnlyList<GrantInfo> Grants, NodaTime.LocalDate Today);
