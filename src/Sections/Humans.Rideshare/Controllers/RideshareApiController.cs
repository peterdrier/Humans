using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Rideshare.Controllers;

/// <summary>The board's GeoJSON feed for MapLibre. Mirrors <c>CityPlanningApiController</c>.</summary>
[ApiController]
[Authorize(Policy = PolicyNames.AppAccess)]
[Route("api/rideshare")]
internal sealed class RideshareApiController(
    IRideshareService rideshare,
    IUserServiceRead users,
    IStringLocalizer<RideshareResource> localizer,
    IClock clock) : ApiControllerBase(users)
{
    [HttpGet("board")]
    public async Task<IActionResult> Board([FromQuery] string? date, [FromQuery] RideshareDirection? direction, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrUnauthorizedAsync(ct);
        if (error is not null) return error;

        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        var dir = direction ?? RideshareDirection.Inbound;
        var day = RideshareDates.Parse(date) ?? BoardViewModel.DefaultDate(snapshot.Settings, dir, RideshareDates.Today(clock));

        var trips = snapshot.JoinableTrips(day, dir);
        var requests = snapshot.ActiveRequests(day, dir);
        var people = await UserService.GetUserInfosAsync(
            trips.Select(t => t.UserId).Concat(requests.Select(r => r.UserId)).Distinct().ToList(), ct);

        var json = BoardFeatureCollection.Build(snapshot, day, dir, user.Id, people, localizer);
        return Content(json, "application/geo+json");
    }
}
