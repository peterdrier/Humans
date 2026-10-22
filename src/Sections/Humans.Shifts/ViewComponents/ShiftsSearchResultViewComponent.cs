using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Public because Razor discovery filters on public: an internal view component ships <c>&lt;vc:…&gt;</c> as inert
/// markup on a green build. Both reads are cache-served.
/// </summary>
public sealed class ShiftsSearchResultViewComponent(
    IShiftService shifts,
    ITeamService teams) : ViewComponent
{
    /// <summary>Nothing when the rota no longer resolves.</summary>
    public async Task<IViewComponentResult> InvokeAsync(Guid rotaId)
    {
        var ct = HttpContext.RequestAborted;
        var rota = await shifts.GetRotaAsync(rotaId, ct);
        if (rota is null)
            return Content(string.Empty);

        var team = await teams.GetTeamAsync(rota.TeamId, ct);
        return View(new ShiftsSearchResultViewModel(rota.Name, rota.TeamId, team?.Name ?? string.Empty));
    }
}

internal sealed record ShiftsSearchResultViewModel(string Name, Guid TeamId, string TeamName);
