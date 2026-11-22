using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.CityPlanning.ViewComponents;

/// <summary>
/// The placement phase box on a camp's page, contributed through <see cref="ICampPart"/>. Shown to the camp's leads
/// for the active event and to map admins; renders nothing for anyone else or when no event is active.
/// </summary>
internal sealed class CampPlacementPhaseViewComponent(
    CityPlanningService cityPlanning,
    IUserService users,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid campId)
    {
        var ct = HttpContext.RequestAborted;
        if (await cityPlanning.GetSettingsAsync(ct) is not { } settings)
            return Content(string.Empty);

        if (!await CanSeeAsync(campId, settings.Year, ct))
            return Content(string.Empty);

        return View(new CampPlacementPhaseViewModel(
            settings.PlacementState,
            (settings.PlacementOpensOn, settings.PlacementClosesOn, settings.PlacementChangeableUntilOn)));
    }

    private async Task<bool> CanSeeAsync(Guid campId, int year, CancellationToken ct)
    {
        if ((await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.CityPlanningMapAdmin)).Succeeded)
            return true;

        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var claimedId)
            || await users.GetUserInfoAsync(claimedId, ct) is not { } viewer)
            return false;

        return (await cityPlanning.GetLeadCampsAsync(viewer.Id, year, ct))
            .Any(camp => camp.Id == campId);
    }
}

internal sealed record CampPlacementPhaseViewModel(
    BarrioPlacementState State,
    (LocalDate? OpensOn, LocalDate? ClosesOn, LocalDate? ChangeableUntilOn) Schedule);
