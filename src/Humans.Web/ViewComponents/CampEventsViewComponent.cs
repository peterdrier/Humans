using System.Security.Claims;
using Humans.Application.Interfaces.Events;
using Humans.Web.Helpers;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders a compact card of a camp's approved events on the camp detail page,
/// with the Browse-style per-row favourite toggle. Invoked with the camp's id
/// and slug only — no Event types cross into the Camps view. Auth-gated at the
/// call site (logged-in Humans users); returns empty content when the Events
/// feature is disabled or the camp has no approved events so the card auto-hides.
/// </summary>
public class CampEventsViewComponent(
    IEventServiceRead events,
    IConfiguration configuration,
    ILogger<CampEventsViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid campId, string campSlug)
    {
        try
        {
            // Mirror EventsFeatureFilter: when the Event Guide is off, the Events
            // routes 404 — so the card (and its favourite POST target) must vanish too.
            if (!configuration.GetValue<bool>("Features:Events"))
                return Content(string.Empty);

            if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Content(string.Empty);

            var approved = await events.GetApprovedEventsAsync(campId, null, null, null, []);
            if (approved.Count == 0)
                return Content(string.Empty);

            var settings = await events.GetGuideSettingsAsync();
            var tz = settings?.TimeZoneId != null
                ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(settings.TimeZoneId)
                : null;

            var favouriteIds = await events.GetFavouriteEventIdsAsync(userId);

            var rows = approved
                .OrderBy(e => e.StartAt)
                .Select(e => new CampEventsCardRow
                {
                    EventId = e.Id,
                    Title = e.Title,
                    CategoryName = e.CategoryName,
                    Description = e.Description,
                    StartAt = EventsTimeHelpers.ToLocalDateTime(e.StartAt, tz),
                    DurationMinutes = e.DurationMinutes,
                    VenueName = e.VenueName,
                    LocationNote = e.LocationNote,
                    Host = e.Host,
                    IsRecurring = e.IsRecurring,
                    IsFavourited = favouriteIds.Contains(e.Id)
                })
                .ToList();

            return View(new CampEventsCardViewModel { CampSlug = campSlug, Rows = rows });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load events card for camp {CampId}", campId);
            return Content(string.Empty);
        }
    }
}
