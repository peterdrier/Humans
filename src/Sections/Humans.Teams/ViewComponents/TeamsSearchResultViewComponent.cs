using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.ViewComponents;

/// <summary>
/// One team-search result row, keyed by slug
/// (nobodies-collective/Humans#1062). Callers hold the key their own search
/// produced; Teams owns what a team row looks like and fetches its own display
/// data. Replaces the projection <c>SearchService</c> used to build.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal
/// view component ships <c>&lt;vc:…&gt;</c> as inert markup on a green build
/// (HUM0034's framework exception). Slug rather than id: <c>TeamSearchHit</c>
/// carries no id, and the slug is already the team's canonical URL key.
/// </remarks>
public sealed class TeamsSearchResultViewComponent(ITeamServiceRead teams) : ViewComponent
{
    /// <param name="slug">The matched team's URL slug.</param>
    public async Task<IViewComponentResult> InvokeAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Content(string.Empty);

        // Served from the cached TeamInfo snapshot — one row costs no query.
        var team = await teams.GetTeamBySlugAsync(slug);
        return team is null
            ? Content(string.Empty)
            : View(new TeamsSearchResultViewModel(team.Name, team.Slug));
    }
}

internal sealed record TeamsSearchResultViewModel(string Name, string Slug);
