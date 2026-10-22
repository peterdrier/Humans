using System.Security.Claims;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.ViewComponents;

/// <summary>
/// Never on the viewer's own profile, and only when the member accepts facilitated messages. These buttons only offer:
/// <c>TeamMessageController</c> re-checks the team.
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

internal sealed record TeamMessageButtonsViewModel(
    Guid UserId,
    string DisplayName,
    IReadOnlyList<TeamMessageOption> Teams);
