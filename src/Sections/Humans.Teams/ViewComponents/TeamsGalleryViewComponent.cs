using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.ViewComponents;

/// <summary>
/// The widget gallery's Teams samples: the search-result row keyed off the first real team (it renders nothing for an
/// unresolved key); null when the environment holds none.
/// </summary>
internal sealed class TeamsGalleryViewComponent(ITeamService teamService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var teams = await teamService.GetTeamsAsync(HttpContext.RequestAborted);
        return View(new TeamsGalleryViewModel(teams.Values.FirstOrDefault()?.Id));
    }
}

internal sealed record TeamsGalleryViewModel(Guid? TeamId);
