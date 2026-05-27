using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class VolunteerBuildStripViewComponent(
    IVolunteerTrackingServiceRead tracking,
    IUserServiceRead userService,
    ILogger<VolunteerBuildStripViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        try
        {
            var strip = await tracking.GetUserBuildStripAsync(userId);
            if (strip is null) return Content(string.Empty);

            var info = await userService.GetUserInfoAsync(userId);
            var names = new Dictionary<Guid, string> { [userId] = info?.BurnerName ?? string.Empty };

            var model = new HeatmapPartialModel(
                [strip.Row],
                strip.BuildStartOffset,
                strip.GateOpeningDate,
                names,
                ShowAvailabilityControls: true);

            return View("~/Views/VolunteerTracking/_VolunteerHeatmap.cshtml", model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading build strip for user {UserId}", userId);
            return Content(string.Empty);
        }
    }
}
