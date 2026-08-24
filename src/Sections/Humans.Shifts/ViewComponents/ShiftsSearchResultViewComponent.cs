using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// One rota-search result row, keyed by rota id
/// (nobodies-collective/Humans#1062). Callers hold the id their own search
/// produced; Shifts owns what a rota row looks like, including the owning
/// team's name and the department-filtered link the row points at.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal
/// view component ships <c>&lt;vc:…&gt;</c> as inert markup on a green build
/// (HUM0034's framework exception). Both reads are cache-served, so a bucket of
/// rows costs no query once warm.
/// </remarks>
public sealed class ShiftsSearchResultViewComponent(
    IShiftManagementServiceRead shifts,
    ITeamServiceRead teams) : ViewComponent
{
    /// <summary>Renders the row, or nothing when the rota no longer resolves.</summary>
    /// <param name="rotaId">The matched rota.</param>
    public async Task<IViewComponentResult> InvokeAsync(Guid rotaId)
    {
        var rota = await shifts.GetRotaAsync(rotaId);
        if (rota is null)
            return Content(string.Empty);

        var team = await teams.GetTeamAsync(rota.TeamId);
        return View(new ShiftsSearchResultViewModel(rota.Name, rota.TeamId, team?.Name ?? string.Empty));
    }
}

internal sealed record ShiftsSearchResultViewModel(string Name, Guid TeamId, string TeamName);
