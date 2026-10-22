using Humans.Shifts.Models;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Public, under <c>Contracts/</c>, so the tag helper is generated; the model it builds and the partial it renders
/// stay internal.
/// </summary>
public sealed class VolunteerBuildStripViewComponent(
    IVolunteerTrackingService tracking,
    IUserService userService,
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading build strip for user {UserId}", userId);
            return Content(string.Empty);
        }
    }
}
