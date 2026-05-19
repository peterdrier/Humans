using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Web.Filters;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers.Api;

[ApiController]
[Route("api/events")]
[EnableCors("EventsApi")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class EventsApiController(IEventService guide, ICampService camps, UserManager<User> userManager)
    : ControllerBase
{
    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] int? day,
        [FromQuery] string? categorySlug,
        [FromQuery] Guid? barrioId,
        [FromQuery] string? q)
    {
        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = eventSettings?.GateOpeningDate;

        var excludedSlugs = await GetExcludedSlugsAsync();
        var events = await guide.GetApprovedEventsAsync(barrioId, null, null, q, excludedSlugs);
        var campsById = await LoadCampsByIdAsync(gateOpeningDate?.Year);

        var results = new List<GuideEventApiDto>();
        foreach (var e in events)
        {
            if (categorySlug != null && !string.Equals(e.Category.Slug, categorySlug, StringComparison.OrdinalIgnoreCase))
                continue;

            var campName = ResolveCampName(e.CampId, campsById);

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
        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;

        var e = await guide.GetApprovedEventByIdAsync(id);
        if (e == null) return NotFound();

        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var campsById = await LoadCampsByIdAsync(gateOpeningDate?.Year);
        var campName = ResolveCampName(e.CampId, campsById);

        return Ok(BuildEventDto(e, e.StartAt, ComputeDayOffset(e.StartAt, gateOpeningDate, tz), campName));
    }

    [HttpGet("barrios")]
    public async Task<IActionResult> GetBarrios()
    {
        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        var campsById = await LoadCampsByIdAsync(eventSettings?.GateOpeningDate.Year);

        var events = await guide.GetApprovedEventsAsync(null, null, null, null, []);
        var barrioGroups = events
            .Where(e => e.CampId.HasValue)
            .GroupBy(e => e.CampId!.Value)
            .Select(g =>
            {
                var camp = campsById.GetValueOrDefault(g.Key);
                var seasonName = camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name;
                return new GuideCampApiDto(
                    g.Key,
                    seasonName ?? camp?.Slug,
                    camp?.Slug);
            })
            .ToList();

        return Ok(barrioGroups);
    }

    [HttpGet("barrios/{id:guid}")]
    public async Task<IActionResult> GetBarrio(Guid id)
    {
        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = eventSettings?.GateOpeningDate;

        var events = await guide.GetApprovedEventsAsync(id, null, null, null, []);
        if (!events.Any()) return NotFound();

        var campsById = await LoadCampsByIdAsync(gateOpeningDate?.Year);
        var camp = campsById.GetValueOrDefault(id);
        var seasonName = camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name;
        var campName = seasonName ?? camp?.Slug;

        return Ok(new GuideCampDetailApiDto(
            id,
            campName,
            camp?.Slug,
            events.Select(e =>
            {
                var dayOffset = ComputeDayOffset(e.StartAt, gateOpeningDate, tz);
                return BuildEventDto(e, e.StartAt, dayOffset, campName);
            }).ToList()));
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        var categories = await guide.GetActiveCategoriesAsync();
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
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var slugs = await guide.GetExcludedCategorySlugsAsync(user.Id);
        return Ok(new { excludedCategorySlugs = slugs });
    }

    [Authorize]
    [DisableCors]
    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var activeCategories = await guide.GetActiveCategoriesAsync();
        var activeSlugs = activeCategories.Select(c => c.Slug).ToList();
        var invalidSlugs = request.ExcludedCategorySlugs
            .Where(s => !activeSlugs.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (invalidSlugs.Count > 0)
            return BadRequest(new { error = $"Invalid category slugs: {string.Join(", ", invalidSlugs)}" });

        await guide.SavePreferenceAsync(user.Id, request.ExcludedCategorySlugs);
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
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = eventSettings?.GateOpeningDate;

        var favourites = await guide.GetFavouritesWithEventsAsync(user.Id);
        var campsById = await LoadCampsByIdAsync(gateOpeningDate?.Year);
        var results = favourites.Select(f =>
        {
            var e = f.Event;
            var campName = ResolveCampName(e.CampId, campsById);
            return BuildEventDto(e, e.StartAt, ComputeDayOffset(e.StartAt, gateOpeningDate, tz), campName);
        }).ToList();

        return Ok(results);
    }

    [Authorize]
    [DisableCors]
    [HttpPost("favourites/{eventId:guid}")]
    public async Task<IActionResult> AddFavourite(Guid eventId)
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var added = await guide.AddFavouriteAsync(user.Id, eventId);
        if (!added) return Conflict(new { error = "Already favourited" });
        return Ok(new { favourited = true });
    }

    [Authorize]
    [DisableCors]
    [HttpDelete("favourites/{eventId:guid}")]
    public async Task<IActionResult> RemoveFavourite(Guid eventId)
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var removed = await guide.RemoveFavouriteAsync(user.Id, eventId);
        if (!removed) return NotFound();
        return Ok(new { unfavourited = true });
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<List<string>> GetExcludedSlugsAsync()
    {
        if (User.Identity?.IsAuthenticated != true) return [];
        var user = await userManager.GetUserAsync(User);
        if (user == null) return [];
        return await guide.GetExcludedCategorySlugsAsync(user.Id);
    }

    private async Task<BurnSettingsInfo?> LoadBurnSettingsAsync(EventGuideSettings? guideSettings)
    {
        if (guideSettings == null) return null;
        return await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId);
    }

    private async Task<Dictionary<Guid, CampInfo>> LoadCampsByIdAsync(int? year)
    {
        if (year is null) return [];
        var camps1 = await camps.GetCampsForYearAsync(year.Value);
        return camps1.ToDictionary(c => c.Id);
    }

    private static string? ResolveCampName(Guid? campId, IReadOnlyDictionary<Guid, CampInfo> campsById)
    {
        if (campId is null) return null;
        var camp = campsById.GetValueOrDefault(campId.Value);
        var seasonName = camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name;
        return seasonName ?? camp?.Slug;
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
