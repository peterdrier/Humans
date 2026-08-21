using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.ViewComponents;

/// <summary>
/// One team-search result row, keyed by team id
/// (nobodies-collective/Humans#1062). Callers hold the id their own search
/// produced; Teams owns what a team row looks like and fetches its own display
/// data — including the slug the row links to. Replaces the projection
/// <c>SearchService</c> used to build.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal
/// view component ships <c>&lt;vc:…&gt;</c> as inert markup on a green build
/// (HUM0034's framework exception).
/// </remarks>
public sealed class TeamsSearchResultViewComponent(ITeamServiceRead teams) : ViewComponent
{
    /// <param name="teamId">The matched team.</param>
    public async Task<IViewComponentResult> InvokeAsync(Guid teamId)
    {
        // Served from the cached TeamInfo snapshot — one row costs no query.
        var team = await teams.GetTeamAsync(teamId);
        return team is null
            ? Content(string.Empty)
            : View(new TeamsSearchResultViewModel(team.Name, team.Slug));
    }
}

internal sealed record TeamsSearchResultViewModel(string Name, string Slug);
