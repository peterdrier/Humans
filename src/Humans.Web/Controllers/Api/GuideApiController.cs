using Humans.Application.Interfaces.Events;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers.Api;

[ApiController]
[Route("api/events")]
[EnableCors("GuideApi")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class GuideApiController : ControllerBase
{
    private readonly IEventService _guide;
    private readonly UserManager<User> _userManager;

    public GuideApiController(IEventService guide, UserManager<User> userManager)
    {
        _guide = guide;
        _userManager = userManager;
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] int? day,
        [FromQuery] string? categorySlug,
        [FromQuery] Guid? barrioId,
        [FromQuery] string? q)
    {
        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = guideSettings?.EventSettings?.GateOpeningDate;

        var excludedSlugs = await GetExcludedSlugsAsync();
        var events = await _guide.GetApprovedEventsAsync(barrioId, null, null, q, excludedSlugs);

        var results = new List<GuideEventApiDto>();
        foreach (var e in events)
        {
            if (categorySlug != null && !string.Equals(e.Category.Slug, categorySlug, StringComparison.OrdinalIgnoreCase))
                continue;

            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;

            foreach (var occurrenceStart in e.GetOccurrenceInstants())
            {
                var eventDayOffset = ComputeDayOffset(occurrenceStart, gateOpeningDate, tz);
                if (day.HasValue && eventDayOffset != day.Value) continue;
                results.Add(BuildEventDto(e, occurrenceStart, eventDayOffset, campName));
            }
        }

        return Ok(results);
    }

    [HttpGet("events/{id:guid}")]
    public async Task<IActionResult> GetEvent(Guid id)
    {
        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var e = await _guide.GetApprovedEventByIdAsync(id);
        if (e == null) return NotFound();

        var gateOpeningDate = guideSettings?.EventSettings?.GateOpeningDate;
        var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        var campName = campSeason?.Name ?? e.Camp?.Slug;

        return Ok(BuildEventDto(e, e.StartAt, ComputeDayOffset(e.StartAt, gateOpeningDate, tz), campName));
    }

    [HttpGet("barrios")]
    public async Task<IActionResult> GetBarrios()
    {
        var events = await _guide.GetApprovedEventsAsync(null, null, null, null, []);
        var barrioGroups = events
            .Where(e => e.CampId.HasValue)
            .GroupBy(e => e.CampId!.Value)
            .Select(g =>
            {
                var first = g.First();
                var season = first.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
                return new GuideCampApiDto(
                    first.CampId!.Value,
                    season?.Name ?? first.Camp?.Slug,
                    first.Camp?.Slug);
            })
            .ToList();

        return Ok(barrioGroups);
    }

    [HttpGet("barrios/{id:guid}")]
    public async Task<IActionResult> GetBarrio(Guid id)
    {
        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = guideSettings?.EventSettings?.GateOpeningDate;

        var events = await _guide.GetApprovedEventsAsync(id, null, null, null, []);
        if (!events.Any()) return NotFound();

        var first = events.First();
        var season = first.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        var campName = season?.Name ?? first.Camp?.Slug;

        return Ok(new GuideCampDetailApiDto(
            id,
            campName,
            first.Camp?.Slug,
            events.Select(e =>
            {
                var dayOffset = ComputeDayOffset(e.StartAt, gateOpeningDate, tz);
                return BuildEventDto(e, e.StartAt, dayOffset, campName);
            }).ToList()));
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        var categories = await _guide.GetActiveCategoriesAsync();
        return Ok(categories.Select(c => new GuideCategoryApiDto(
            c.Id,
            c.Name,
            c.Slug,
            c.IsSensitive,
            c.DisplayOrder)));
    }

    // ─── Preferences (authenticated, same-origin — no CORS) ───────

    [Authorize]
    [DisableCors]
    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var slugs = await _guide.GetExcludedCategorySlugsAsync(user.Id);
        return Ok(new { excludedCategorySlugs = slugs });
    }

    [Authorize]
    [DisableCors]
    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var activeCategories = await _guide.GetActiveCategoriesAsync();
        var activeSlugs = activeCategories.Select(c => c.Slug).ToList();
        var invalidSlugs = request.ExcludedCategorySlugs
            .Where(s => !activeSlugs.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (invalidSlugs.Count > 0)
            return BadRequest(new { error = $"Invalid category slugs: {string.Join(", ", invalidSlugs)}" });

        await _guide.SavePreferenceAsync(user.Id, request.ExcludedCategorySlugs);
        return Ok(new { excludedCategorySlugs = request.ExcludedCategorySlugs });
    }

    public sealed class UpdatePreferencesRequest
    {
        public List<string> ExcludedCategorySlugs { get; set; } = [];
    }

    // ─── Favourites (authenticated, same-origin — no CORS) ────────

    [Authorize]
    [DisableCors]
    [HttpGet("favourites")]
    public async Task<IActionResult> GetFavourites()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = guideSettings?.EventSettings?.GateOpeningDate;

        var favourites = await _guide.GetFavouritesWithEventsAsync(user.Id);
        var results = favourites.Select(f =>
        {
            var e = f.Event;
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            return BuildEventDto(e, e.StartAt, ComputeDayOffset(e.StartAt, gateOpeningDate, tz), campName);
        }).ToList();

        return Ok(results);
    }

    [Authorize]
    [DisableCors]
    [HttpPost("favourites/{eventId:guid}")]
    public async Task<IActionResult> AddFavourite(Guid eventId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var added = await _guide.AddFavouriteAsync(user.Id, eventId);
        if (!added) return Conflict(new { error = "Already favourited" });
        return Ok(new { favourited = true });
    }

    [Authorize]
    [DisableCors]
    [HttpDelete("favourites/{eventId:guid}")]
    public async Task<IActionResult> RemoveFavourite(Guid eventId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var removed = await _guide.RemoveFavouriteAsync(user.Id, eventId);
        if (!removed) return NotFound();
        return Ok(new { unfavourited = true });
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<List<string>> GetExcludedSlugsAsync()
    {
        if (User.Identity?.IsAuthenticated != true) return [];
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return [];
        return await _guide.GetExcludedCategorySlugsAsync(user.Id);
    }

    private static GuideEventApiDto BuildEventDto(
        Event e, Instant startAt, int dayOffset, string? campName)
    {
        return new GuideEventApiDto(
            e.Id,
            e.Title,
            e.Description,
            new GuideEventCategoryApiDto(
                e.Category.Id,
                e.Category.Name,
                e.Category.Slug,
                e.Category.IsSensitive),
            InstantPattern.General.Format(startAt),
            e.DurationMinutes,
            dayOffset,
            e.IsRecurring,
            e.CampId.HasValue ? new GuideEventCampApiDto(e.CampId.Value, campName) : null,
            e.GuideSharedVenueId.HasValue && e.EventVenue != null
                ? new GuideEventVenueApiDto(e.GuideSharedVenueId.Value, e.EventVenue.Name)
                : null,
            e.LocationNote,
            e.PriorityRank);
    }

    private static int ComputeDayOffset(Instant instant, LocalDate? gateOpeningDate, DateTimeZone? tz)
    {
        if (gateOpeningDate == null) return 0;
        var eventDate = tz != null ? instant.InZone(tz).Date : LocalDate.FromDateTime(instant.ToDateTimeUtc());
        return Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
    }
}
