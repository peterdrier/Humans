using System.Security.Claims;
using Humans.Base.Enums;
using Humans.Grants.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Grants.ViewComponents;

/// <summary>
/// The "My grants" card, contributed to <c>UserPartSlots.Profile</c>. Own profile only: renders
/// for <see cref="ProfileCardViewMode.Self"/> when the signed-in user is the target, and nothing
/// for anyone else. Shows the person's active grants and the ones that ended this year or
/// later; nothing when there are none.
/// </summary>
/// <remarks>
/// Internal: invoked by Type through the Shell's slot renderer, and its constructor takes the
/// section-internal <see cref="IGrantManagement"/>.
/// </remarks>
internal sealed class MyGrantsViewComponent(IGrantManagement grants) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Self
            || !Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId)
            || viewerId != userId)
            return Content(string.Empty);

        var today = grants.Today;
        var mine = (await grants.GetGrantsForUserAsync(userId, HttpContext.RequestAborted))
            .Where(g => g.State(today) == GrantState.Active || EndedIn(g) >= today.Year)
            .OrderBy(g => g.State(today)).ThenByDescending(g => g.IssuedAt)
            .ToList();
        if (mine.Count == 0) return Content(string.Empty);

        return View(new GrantsCardViewModel(mine, today, new HashSet<Guid>()));
    }

    /// <summary>The year a grant that is no longer active ended: its revocation, else its expiry.</summary>
    private static int? EndedIn(GrantInfo grant) =>
        grant.RevokedAt is { } revoked ? revoked.InZone(GrantService.MadridZone).Year : grant.ExpiresOn?.Year;
}
