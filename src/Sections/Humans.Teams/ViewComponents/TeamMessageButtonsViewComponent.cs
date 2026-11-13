using System.Security.Claims;
using Humans.Base.Enums;
using Humans.Teams.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.ViewComponents;

/// <summary>
/// Never on the viewer's own profile, and only when the member accepts facilitated messages. These buttons only offer:
/// <c>TeamMessageController</c> re-checks the team.
/// </summary>
internal sealed class TeamMessageButtonsViewComponent(
    ITeamMessageOptionsProvider options) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode == ProfileCardViewMode.Self
            || !Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId)
            || viewerId == userId)
            return Content(string.Empty);

        var ct = HttpContext.RequestAborted;
        var context = await options.GetContextAsync(viewerId, userId, ct);
        if (context.Refusal is not null)
            return Content(string.Empty);

        var teams = context.Teams.OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase).ToList();
        if (teams.Count == 0)
            return Content(string.Empty);

        return View(new TeamMessageButtonsViewModel(context.Recipient!.Id, teams));
    }
}

internal sealed record TeamMessageButtonsViewModel(
    Guid UserId,
    IReadOnlyList<TeamMessageOption> Teams);
