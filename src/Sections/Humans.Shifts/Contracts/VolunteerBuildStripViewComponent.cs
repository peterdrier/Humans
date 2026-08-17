using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The single-user build strip rendered as <c>&lt;vc:volunteer-build-strip&gt;</c> on
/// Shell's profile page.
/// </summary>
/// <remarks>
/// Public, under <c>Contracts/</c>, so the tag helper is generated and the one call site
/// is unchanged — its constructor takes only the leaf read interface and Base services,
/// so nothing forces the feature-provider path (design §15 step 6). The
/// <c>HeatmapPartialModel</c> it builds and the partial it renders stay internal; only
/// <c>InvokeAsync(Guid)</c> is public (nobodies-collective/Humans#866).
/// </remarks>
public sealed class VolunteerBuildStripViewComponent(
    IVolunteerTrackingServiceRead tracking,
    IUserServiceRead userService,
    ILogger<VolunteerBuildStripViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var strip = await tracking.GetUserBuildStripAsync(userId, ct);
            if (strip is null) return Content(string.Empty);

            var info = await userService.GetUserInfoAsync(userId, ct);
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
