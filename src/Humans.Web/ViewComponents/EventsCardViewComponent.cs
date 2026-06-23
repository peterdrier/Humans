using System.Security.Claims;
using Humans.Application.Interfaces.Events;
using Humans.Web.Helpers;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders a compact card of approved events with the Browse-style per-row
/// favourite toggle. Scoped to exactly one of: a camp (the camp detail page's
/// events card) or a user's personal (non-camp) submitted events — their
/// profile page; camp events they submitted live on the camp's page. Invoked with
/// ids only — no Event types cross into the host section's views. The favourite
/// heart toggles in place via the JSON favourites API (no page reload). Auth-gated
/// at the call site (logged-in Humans users); returns empty content when the Events feature
/// is disabled or there are no approved events in scope so the card auto-hides.
/// </summary>
public class EventsCardViewComponent(
    IEventServiceRead events,
    IConfiguration configuration,
    ILogger<EventsCardViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid? campId = null, Guid? userId = null)
    {
        try
        {
            // Mirror EventsFeatureFilter: when the Event Guide is off, the Events
            // routes 404 — so the card (and its favourite API target) must vanish too.
            if (!configuration.GetValue<bool>("Features:Events"))
                return Content(string.Empty);

            if (campId is null == userId is null)
            {
                logger.LogError("EventsCard invoked with campId={CampId}, userId={UserId} — exactly one scope required", campId, userId);
                return Content(string.Empty);
            }

            if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId))
                return Content(string.Empty);

            var approved = await events.GetApprovedEventsAsync(campId, null, null, null, []);
            if (userId is not null)
                approved = approved.Where(e => e.SubmitterUserId == userId && e.CampId is null).ToList();
            if (approved.Count == 0)
                return Content(string.Empty);

            var settings = await events.GetGuideSettingsAsync();
            var tz = settings?.TimeZoneId != null
                ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(settings.TimeZoneId)
                : null;

            var favouriteIds = await events.GetFavouriteEventIdsAsync(viewerId);

            var rows = approved
                .OrderBy(e => e.StartAt)
                .Select(e => new EventsCardRow
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

            return View(new EventsCardViewModel
            {
                Rows = rows
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load events card (campId={CampId}, userId={UserId})", campId, userId);
            return Content(string.Empty);
        }
    }
}
