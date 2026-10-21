using Humans.Email.Contracts;
using System.Security.Claims;
using Humans.Base.Enums;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>
/// The "Send a message from {team}" buttons, contributed to <c>UserPartSlots.Profile</c>. They
/// show on another member's profile (never the viewer's own) when that member accepts facilitated
/// messages, one per team the viewer may send as (<see cref="ITeamMessageOptionsProvider"/>);
/// nothing otherwise. The send endpoint re-checks the team, so these buttons only offer.
/// </summary>
internal sealed class TeamMessageButtonsViewComponent(
    ITeamMessageOptionsProvider options,
    ICommunicationPreferenceService commPrefs,
    IUserService users) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode == ProfileCardViewMode.Self
            || !Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId)
            || viewerId == userId)
            return Content(string.Empty);

        var ct = HttpContext.RequestAborted;
        if (!await commPrefs.AcceptsFacilitatedMessagesAsync(userId, ct))
            return Content(string.Empty);

        var teams = (await options.GetOptionsAsync(viewerId, ct))
            .OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (teams.Count == 0)
            return Content(string.Empty);

        var recipient = await users.GetUserInfoAsync(userId, ct);
        return View(new TeamMessageButtonsViewModel(userId, recipient?.BurnerName ?? string.Empty, teams));
    }
}

/// <summary>What the team message buttons render.</summary>
internal sealed record TeamMessageButtonsViewModel(
    Guid UserId,
    string DisplayName,
    IReadOnlyList<TeamMessageOption> Teams);
