using Humans.Base.Enums;
using Humans.Grants.Authorization;
using Humans.Grants.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Grants.ViewComponents;

/// <summary>
/// Renders only on the viewer's own profile. The viewer is resolved through Users, so a cookie still carrying a
/// merged-away id matches the surviving account the page passes in. Shows active grants and ones that ended this year
/// or later.
/// </summary>
internal sealed class MyGrantsViewComponent(IGrantManagement grants, IUserService users) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Self
            || await GrantsPolicyHandler.ViewerAsync(users, UserClaimsPrincipal, HttpContext.RequestAborted) is not { } viewer
            || !viewer.AllUserIds.Contains(userId))
            return Content(string.Empty);

        var today = grants.Today;
        var mine = (await grants.GetGrantsForUserAsync(userId, HttpContext.RequestAborted))
            .Where(g => g.State(today) == GrantState.Active || EndedIn(g) >= today.Year)
            .OrderBy(g => g.State(today)).ThenByDescending(g => g.IssuedAt)
            .ToList();
        if (mine.Count == 0) return Content(string.Empty);

        return View(new GrantsCardViewModel(mine, today, new HashSet<Guid>()));
    }

    /// <summary>Its revocation, else its expiry.</summary>
    private static int? EndedIn(GrantInfo grant) =>
        grant.RevokedAt is { } revoked ? revoked.InZone(GrantService.MadridZone).Year : grant.ExpiresOn?.Year;
}
