using System.Security.Claims;
using Humans.Events.Contracts;
using Humans.Events.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NodaTime;

namespace Humans.Events.ViewComponents;

/// <summary>
/// Scoped to one camp (its detail page) or one user's personal, non-camp submissions (their profile). Invoked with
/// ids only, so no Event types cross into the host's views. Renders nothing when the feature is off or nothing is in
/// scope, so the card hides itself.
/// </summary>
// Public: Razor's tag-helper discovery only sees public view components; an internal one renders as inert markup.
public sealed class EventsCardViewComponent(
    IEventService events,
    IConfiguration configuration,
    ILogger<EventsCardViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid? campId = null, Guid? userId = null)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            // The Events routes 404 when the guide is off, so the card and its favourite API target must vanish too.
            if (!configuration.GetValue<bool>("Features:Events"))
                return Content(string.Empty);

            if (campId is null == userId is null)
            {
                logger.LogError("EventsCard invoked with campId={CampId}, userId={UserId} — exactly one scope required", campId, userId);
                return Content(string.Empty);
            }

            if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId))
                return Content(string.Empty);

            var approved = await events.GetApprovedEventsAsync(campId, null, null, null, [], ct);
            if (userId is not null)
                approved = approved.Where(e => e.SubmitterUserId == userId && e.CampId is null).ToList();
            if (approved.Count == 0)
                return Content(string.Empty);

            var settings = await events.GetGuideSettingsAsync(ct);
            var tz = settings?.TimeZoneId != null
                ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(settings.TimeZoneId)
                : null;

            var favouriteIds = await events.GetFavouriteEventIdsAsync(viewerId, ct);

            var rows = approved
                .OrderBy(e => e.StartAt)
                .Select(e => new EventsCardRow
                {
                    EventId = e.Id,
                    Title = e.Title,
                    CategoryName = e.CategoryName,
                    Description = e.Description,
                    StartAt = tz is null ? e.StartAt.ToDateTimeUtc() : e.StartAt.InZone(tz).ToDateTimeUnspecified(),
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load events card (campId={CampId}, userId={UserId})", campId, userId);
            return Content(string.Empty);
        }
    }
}
