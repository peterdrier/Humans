using Humans.Application.Interfaces.EventGuide;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers.Api;

[ApiController]
[Route("api/guide")]
[EnableCors("GuideApi")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class GuideApiController : ControllerBase
{
    private readonly IEventGuideService _guide;
    private readonly UserManager<User> _userManager;

    public GuideApiController(IEventGuideService guide, UserManager<User> userManager)
    {
        _guide = guide;
        _userManager = userManager;
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] int? day,
        [FromQuery] string? categorySlug,
        [FromQuery] Guid? campId,
        [FromQuery] string? q)
    {
        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = guideSettings?.EventSettings?.GateOpeningDate;

        var excludedSlugs = await GetExcludedSlugsAsync();
        var events = await _guide.GetApprovedEventsAsync(campId, null, null, q, excludedSlugs);

        var results = new List<object>();
        foreach (var e in events)
        {
            if (categorySlug != null && !string.Equals(e.Category.Slug, categorySlug, StringComparison.OrdinalIgnoreCase))
                continue;

            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var submitterName = e.CampId == null
                ? (e.SubmitterUser?.Profile?.BurnerName ?? e.SubmitterUser?.GetEffectiveEmail())
                : null;

            if (e.IsRecurring && !string.IsNullOrEmpty(e.RecurrenceDays))
            {
                var offsets = e.RecurrenceDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var offsetStr in offsets)
                {
                    if (!int.TryParse(offsetStr, System.Globalization.CultureInfo.InvariantCulture, out var dayOffset)) continue;
                    var occurrenceStart = e.StartAt.Plus(Duration.FromDays(dayOffset));
                    var eventDayOffset = ComputeDayOffset(occurrenceStart, gateOpeningDate, tz);
                    if (day.HasValue && eventDayOffset != day.Value) continue;
                    results.Add(BuildEventDto(e, occurrenceStart, eventDayOffset, campName, submitterName));
                }
            }
            else
            {
                var eventDayOffset = ComputeDayOffset(e.StartAt, gateOpeningDate, tz);
                if (day.HasValue && eventDayOffset != day.Value) continue;
                results.Add(BuildEventDto(e, e.StartAt, eventDayOffset, campName, submitterName));
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
        var submitterName = e.CampId == null
            ? (e.SubmitterUser?.Profile?.BurnerName ?? e.SubmitterUser?.GetEffectiveEmail())
            : null;

        return Ok(BuildEventDto(e, e.StartAt, ComputeDayOffset(e.StartAt, gateOpeningDate, tz), campName, submitterName));
    }

    [HttpGet("camps")]
    public async Task<IActionResult> GetCamps()
    {
        var events = await _guide.GetApprovedEventsAsync(null, null, null, null, []);
        var campGroups = events
            .Where(e => e.CampId.HasValue)
            .GroupBy(e => e.CampId!.Value)
            .Select(g =>
            {
                var first = g.First();
                var season = first.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
                return new
                {
                    id = first.CampId!.Value,
                    name = season?.Name ?? first.Camp?.Slug,
                    slug = first.Camp?.Slug
                };
            })
            .ToList();

        return Ok(campGroups);
    }

    [HttpGet("camps/{id:guid}")]
    public async Task<IActionResult> GetCamp(Guid id)
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

        return Ok(new
        {
            id,
            name = campName,
            slug = first.Camp?.Slug,
            events = events.Select(e =>
            {
                var dayOffset = ComputeDayOffset(e.StartAt, gateOpeningDate, tz);
                return BuildEventDto(e, e.StartAt, dayOffset, campName, null);
            }).ToList()
        });
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        var categories = await _guide.GetActiveCategoriesAsync();
        return Ok(categories.Select(c => new
        {
            id = c.Id,
            name = c.Name,
            slug = c.Slug,
            isSensitive = c.IsSensitive,
            displayOrder = c.DisplayOrder
        }));
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
            var e = f.GuideEvent;
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var submitterName = e.CampId == null
                ? (e.SubmitterUser?.Profile?.BurnerName ?? e.SubmitterUser?.GetEffectiveEmail())
                : null;
            return BuildEventDto(e, e.StartAt, ComputeDayOffset(e.StartAt, gateOpeningDate, tz), campName, submitterName);
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

    private static object BuildEventDto(
        GuideEvent e, Instant startAt, int dayOffset, string? campName, string? submitterName)
    {
        return new
        {
            id = e.Id,
            title = e.Title,
            description = e.Description,
            category = new
            {
                id = e.Category.Id,
                name = e.Category.Name,
                slug = e.Category.Slug,
                isSensitive = e.Category.IsSensitive
            },
            startAt = InstantPattern.General.Format(startAt),
            durationMinutes = e.DurationMinutes,
            dayOffset,
            isRecurring = e.IsRecurring,
            camp = e.CampId.HasValue ? new { id = e.CampId.Value, name = campName } : (object?)null,
            venue = e.GuideSharedVenueId.HasValue && e.GuideSharedVenue != null
                ? new { id = e.GuideSharedVenueId.Value, name = e.GuideSharedVenue.Name }
                : (object?)null,
            submitterName,
            locationNote = e.LocationNote,
            priorityRank = e.PriorityRank
        };
    }

    private static int ComputeDayOffset(Instant instant, LocalDate? gateOpeningDate, DateTimeZone? tz)
    {
        if (gateOpeningDate == null) return 0;
        var eventDate = tz != null ? instant.InZone(tz).Date : LocalDate.FromDateTime(instant.ToDateTimeUtc());
        return Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
    }
}
