using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Events.Services;
using Humans.Events.Contracts;
using Humans.Base.Controllers;
using Humans.Events.Filters;
using Humans.Events.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Humans.Events.Helpers.EventsLookupHelpers;

using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Events.Controllers;

[Authorize(Policy = EventsPolicies.Read)]
[Route("Events/Dashboard")]
[ServiceFilter(typeof(EventsFeatureFilter))]
internal sealed class EventsDashboardController(IEventManagement guide, ICampService camps, IUserService userService)
    : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = await guide.GetDashboardAsync(ct);
        foreach (var day in model.CoverageByDay)
            day.DayLabel = day.Date.ToWeekdayDayMonth();
        model.CoverageByDay = model.CoverageByDay.OrderBy(day => day.Date).ToList();
        model.CoverageByCategory = model.CoverageByCategory.OrderBy(category => category.DisplayOrder).ToList();

        var campsById = await LoadCampsByIdAsync(camps, model.EventSettingsId, ct);
        foreach (var camp in model.TopCamps)
            camp.CampName = ResolveCampName(campsById.GetValueOrDefault(camp.CampId)) ?? "Unknown";
        model.TopCamps = model.TopCamps.OrderByDescending(camp => camp.SubmittedCount).Take(20).ToList();

        return View(model);
    }
}
