using System.Text.Json;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers.Api;

[ApiController]
[Route("api/guide")]
[EnableCors("GuideApi")]
public class GuideApiController : ControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;

    public GuideApiController(HumansDbContext dbContext, UserManager<User> userManager, IClock clock)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _clock = clock;
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] int? day,
        [FromQuery] string? categorySlug,
        [FromQuery] Guid? campId,
        [FromQuery] string? q)
    {
        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var query = _dbContext.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.Camp).ThenInclude(c => c!.Seasons)
            .Include(e => e.GuideSharedVenue)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Where(e => e.Status == GuideEventStatus.Approved);

        if (categorySlug != null)
            query = query.Where(e => e.Category.Slug == categorySlug);

        if (campId.HasValue)
            query = query.Where(e => e.CampId == campId.Value);

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(e => EF.Functions.ILike(e.Title, $"%{q}%") ||
                                     EF.Functions.ILike(e.Description, $"%{q}%"));

        // Apply category opt-out for authenticated users
        var excludedSlugs = await GetExcludedCategorySlugsAsync();
        if (excludedSlugs.Count > 0)
            query = query.Where(e => !excludedSlugs.Contains(e.Category.Slug));

        var events = await query.OrderBy(e => e.StartAt).ToListAsync();

        var gateOpeningDate = guideSettings?.EventSettings.GateOpeningDate;

        // Expand recurring events and apply day filter
        var results = new List<object>();
        foreach (var e in events)
        {
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var submitterName = e.CampId == null
                ? (e.SubmitterUser.Profile?.BurnerName ?? e.SubmitterUser.Email)
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
        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var e = await _dbContext.GuideEvents
            .Include(ev => ev.Category)
            .Include(ev => ev.Camp).ThenInclude(c => c!.Seasons)
            .Include(ev => ev.GuideSharedVenue)
            .Include(ev => ev.SubmitterUser).ThenInclude(u => u.Profile)
            .FirstOrDefaultAsync(ev => ev.Id == id && ev.Status == GuideEventStatus.Approved);

        if (e == null) return NotFound();

        var gateOpeningDate = guideSettings?.EventSettings.GateOpeningDate;
        var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        var campName = campSeason?.Name ?? e.Camp?.Slug;
        var submitterName = e.CampId == null
            ? (e.SubmitterUser.Profile?.BurnerName ?? e.SubmitterUser.Email)
            : null;
        var dayOffset = ComputeDayOffset(e.StartAt, gateOpeningDate, tz);

        return Ok(BuildEventDto(e, e.StartAt, dayOffset, campName, submitterName));
    }

    [HttpGet("camps")]
    public async Task<IActionResult> GetCamps()
    {
        // Return camps that have at least one approved event
        var campIds = await _dbContext.GuideEvents
            .Where(e => e.Status == GuideEventStatus.Approved && e.CampId != null)
            .Select(e => e.CampId!.Value)
            .Distinct()
            .ToListAsync();

        var camps = await _dbContext.Camps
            .Include(c => c.Seasons)
            .Where(c => campIds.Contains(c.Id))
            .ToListAsync();

        var result = camps.Select(c =>
        {
            var season = c.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            return new
            {
                id = c.Id,
                name = season?.Name ?? c.Slug,
                slug = c.Slug
            };
        }).ToList();

        return Ok(result);
    }

    [HttpGet("camps/{id:guid}")]
    public async Task<IActionResult> GetCamp(Guid id)
    {
        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var camp = await _dbContext.Camps
            .Include(c => c.Seasons)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (camp == null) return NotFound();

        var season = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        var campName = season?.Name ?? camp.Slug;

        var events = await _dbContext.GuideEvents
            .Include(e => e.Category)
            .Where(e => e.CampId == id && e.Status == GuideEventStatus.Approved)
            .OrderBy(e => e.StartAt)
            .ToListAsync();

        var gateOpeningDate = guideSettings?.EventSettings.GateOpeningDate;

        return Ok(new
        {
            id = camp.Id,
            name = campName,
            slug = camp.Slug,
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
        var categories = await _dbContext.EventCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new
            {
                id = c.Id,
                name = c.Name,
                slug = c.Slug,
                isSensitive = c.IsSensitive,
                displayOrder = c.DisplayOrder
            })
            .ToListAsync();

        return Ok(categories);
    }

    // ─── Preferences (authenticated) ──────────────────────────────

    [Authorize]
    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var pref = await _dbContext.UserGuidePreferences
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var slugs = pref != null
            ? JsonSerializer.Deserialize<List<string>>(pref.ExcludedCategorySlugs) ?? []
            : new List<string>();

        return Ok(new { excludedCategorySlugs = slugs });
    }

    [Authorize]
    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        // Validate slugs
        var activeSlugs = await _dbContext.EventCategories
            .Where(c => c.IsActive)
            .Select(c => c.Slug)
            .ToListAsync();

        var invalidSlugs = request.ExcludedCategorySlugs
            .Where(s => !activeSlugs.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (invalidSlugs.Count > 0)
            return BadRequest(new { error = $"Invalid category slugs: {string.Join(", ", invalidSlugs)}" });

        var pref = await _dbContext.UserGuidePreferences
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var slugsJson = JsonSerializer.Serialize(request.ExcludedCategorySlugs);

        if (pref == null)
        {
            pref = new UserGuidePreference
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ExcludedCategorySlugs = slugsJson,
                UpdatedAt = _clock.GetCurrentInstant()
            };
            _dbContext.UserGuidePreferences.Add(pref);
        }
        else
        {
            pref.ExcludedCategorySlugs = slugsJson;
            pref.UpdatedAt = _clock.GetCurrentInstant();
        }

        await _dbContext.SaveChangesAsync();

        return Ok(new { excludedCategorySlugs = request.ExcludedCategorySlugs });
    }

    public sealed class UpdatePreferencesRequest
    {
        public List<string> ExcludedCategorySlugs { get; set; } = [];
    }

    // ─── Favourites (authenticated) ────────────────────────────────

    [Authorize]
    [HttpGet("favourites")]
    public async Task<IActionResult> GetFavourites()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;
        var gateOpeningDate = guideSettings?.EventSettings.GateOpeningDate;

        var favourites = await _dbContext.UserEventFavourites
            .Include(f => f.GuideEvent).ThenInclude(e => e.Category)
            .Include(f => f.GuideEvent).ThenInclude(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(f => f.GuideEvent).ThenInclude(e => e.GuideSharedVenue)
            .Include(f => f.GuideEvent).ThenInclude(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Where(f => f.UserId == user.Id && f.GuideEvent.Status == GuideEventStatus.Approved)
            .OrderBy(f => f.GuideEvent.StartAt)
            .ToListAsync();

        var results = favourites.Select(f =>
        {
            var e = f.GuideEvent;
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var submitterName = e.CampId == null
                ? (e.SubmitterUser.Profile?.BurnerName ?? e.SubmitterUser.Email)
                : null;
            var dayOffset = ComputeDayOffset(e.StartAt, gateOpeningDate, tz);
            return BuildEventDto(e, e.StartAt, dayOffset, campName, submitterName);
        }).ToList();

        return Ok(results);
    }

    [Authorize]
    [HttpPost("favourites/{eventId:guid}")]
    public async Task<IActionResult> AddFavourite(Guid eventId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var eventExists = await _dbContext.GuideEvents
            .AnyAsync(e => e.Id == eventId && e.Status == GuideEventStatus.Approved);
        if (!eventExists) return NotFound();

        var alreadyFavourited = await _dbContext.UserEventFavourites
            .AnyAsync(f => f.UserId == user.Id && f.GuideEventId == eventId);
        if (alreadyFavourited) return Conflict(new { error = "Already favourited" });

        _dbContext.UserEventFavourites.Add(new UserEventFavourite
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GuideEventId = eventId,
            CreatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        return Ok(new { favourited = true });
    }

    [Authorize]
    [HttpDelete("favourites/{eventId:guid}")]
    public async Task<IActionResult> RemoveFavourite(Guid eventId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var favourite = await _dbContext.UserEventFavourites
            .FirstOrDefaultAsync(f => f.UserId == user.Id && f.GuideEventId == eventId);
        if (favourite == null) return NotFound();

        _dbContext.UserEventFavourites.Remove(favourite);
        await _dbContext.SaveChangesAsync();

        return Ok(new { unfavourited = true });
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<List<string>> GetExcludedCategorySlugsAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
            return [];

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return [];

        var pref = await _dbContext.UserGuidePreferences
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        if (pref == null) return [];

        return JsonSerializer.Deserialize<List<string>>(pref.ExcludedCategorySlugs) ?? [];
    }


    private static object BuildEventDto(
        Domain.Entities.GuideEvent e,
        Instant startAt,
        int dayOffset,
        string? campName,
        string? submitterName)
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
            camp = e.CampId.HasValue
                ? new { id = e.CampId.Value, name = campName }
                : null,
            venue = e.GuideSharedVenueId.HasValue && e.GuideSharedVenue != null
                ? new { id = e.GuideSharedVenueId.Value, name = e.GuideSharedVenue.Name }
                : null,
            submitterName,
            locationNote = e.LocationNote,
            priorityRank = e.PriorityRank
        };
    }

    private static int ComputeDayOffset(Instant instant, LocalDate? gateOpeningDate, DateTimeZone? tz)
    {
        if (gateOpeningDate == null) return 0;

        LocalDate eventDate;
        if (tz != null)
            eventDate = instant.InZone(tz).Date;
        else
            eventDate = LocalDate.FromDateTime(instant.ToDateTimeUtc());

        return Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
    }
}
