using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.ViewComponents;

/// <summary>Teams owns the row and fetches its own display data, including the slug it links to.</summary>
/// <remarks>Public: Razor discovers only public view components; an internal one renders as inert markup.</remarks>
public sealed class TeamsSearchResultViewComponent(ITeamService teams) : ViewComponent
{
    /// <summary>Renders nothing when the team no longer resolves.</summary>
    public async Task<IViewComponentResult> InvokeAsync(Guid teamId)
    {
        // From the cached snapshot: a row costs no query.
        var team = await teams.GetTeamAsync(teamId, HttpContext.RequestAborted);
        return team is null
            ? Content(string.Empty)
            : View(new TeamsSearchResultViewModel(team.Name, team.Slug));
    }
}

internal sealed record TeamsSearchResultViewModel(string Name, string Slug);
