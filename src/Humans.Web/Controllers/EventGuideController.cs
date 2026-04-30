using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Web.Filters;

namespace Humans.Web.Controllers;

[Authorize]
[Route("EventGuide")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class EventGuideController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IEmailService _emailService;
    private readonly ILogger<EventGuideController> _logger;

    public EventGuideController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        IEmailService emailService,
        ILogger<EventGuideController> logger)
        : base(userManager)
    {
        _dbContext = dbContext;
        _clock = clock;
        _emailService = emailService;
        _logger = logger;
    }

    [HttpGet("MySubmissions")]
    public async Task<IActionResult> MySubmissions()
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        var now = _clock.GetCurrentInstant();
        var isSubmissionOpen = guideSettings != null &&
                               now >= guideSettings.SubmissionOpenAt &&
                               now <= guideSettings.SubmissionCloseAt;

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        // Individual submissions = CampId is null, submitted by this user
        var events = await _dbContext.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.GuideSharedVenue)
            .Where(e => e.CampId == null && e.SubmitterUserId == user.Id)
            .OrderByDescending(e => e.SubmittedAt)
            .ToListAsync();

        var model = new MySubmissionsViewModel
        {
            IsSubmissionOpen = isSubmissionOpen,
            SubmissionOpenAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionOpenAt, tz) : null,
            SubmissionCloseAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionCloseAt, tz) : null,
            TimeZoneId = guideSettings?.EventSettings?.TimeZoneId,
            SubmittedCount = events.Count,
            ApprovedCount = events.Count(e => e.Status == GuideEventStatus.Approved),
            PendingCount = events.Count(e => e.Status == GuideEventStatus.Pending),
            Events = events.Select(e => new IndividualEventRowViewModel
            {
                Id = e.Id,
                Title = e.Title,
                VenueName = e.GuideSharedVenue?.Name ?? "—",
                CategoryName = e.Category.Name,
                StartAt = ToLocalDateTime(e.StartAt, tz),
                DurationMinutes = e.DurationMinutes,
                Status = e.Status,
                CanEdit = e.Status is GuideEventStatus.Draft or GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested,
                CanWithdraw = e.Status is GuideEventStatus.Draft or GuideEventStatus.Pending
            }).ToList()
        };

        return View(model);
    }

    [HttpGet("Submit")]
    public async Task<IActionResult> Submit()
    {
        var (open, guideSettings) = await CheckSubmissionWindowAsync();
        if (!open)
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var model = await BuildFormAsync(guideSettings!);
        return View("IndividualEventForm", model);
    }

    [HttpPost("Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(IndividualEventFormViewModel model)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var (open, guideSettings) = await CheckSubmissionWindowAsync();
        if (!open)
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(MySubmissions));
        }

        if (!ModelState.IsValid)
        {
            await PopulateDropdownsAsync(model, guideSettings!);
            return View("IndividualEventForm", model);
        }

        var tz = GetTimeZone(guideSettings!);
        var now = _clock.GetCurrentInstant();
        var durationMinutes = model.IsAllDay ? 1440 : model.DurationMinutes;
        var startTime = model.IsAllDay ? TimeSpan.Zero : model.StartTime;

        var guideEvent = new GuideEvent
        {
            Id = Guid.NewGuid(),
            CampId = null,
            GuideSharedVenueId = model.VenueId,
            SubmitterUserId = user.Id,
            CategoryId = model.CategoryId,
            Title = model.Title,
            Description = model.Description,
            LocationNote = model.LocationNote,
            StartAt = ToInstant(model.StartDate.Add(startTime), tz),
            DurationMinutes = durationMinutes,
            IsRecurring = model.IsRecurring,
            RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null,
            PriorityRank = 0,
            Status = GuideEventStatus.Pending,
            SubmittedAt = now,
            LastUpdatedAt = now
        };

        _dbContext.GuideEvents.Add(guideEvent);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} submitted individual event '{Title}' at venue {VenueId}",
            user.Id, model.Title, model.VenueId);

        // Queue submission-received email
        if (user.Email != null)
        {
            var profile = await _dbContext.Users.Include(u => u.Profile).Where(u => u.Id == user.Id).Select(u => u.Profile).FirstOrDefaultAsync();
            var viewUrl = Url.Action(nameof(MySubmissions), "EventGuide", null, Request.Scheme)!;
            await _emailService.SendEventSubmittedAsync(user.Email, profile?.BurnerName ?? user.Email, model.Title, viewUrl);
        }

        SetSuccess($"Event \"{model.Title}\" submitted for review.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Submit/{eventId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid eventId)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var guideEvent = await _dbContext.GuideEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == user.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (GuideEventStatus.Draft or GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested))
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();
        if (guideSettings == null)
        {
            SetError("Guide settings not configured.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var tz = GetTimeZone(guideSettings);
        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);

        var model = await BuildFormAsync(guideSettings);
        model.Id = guideEvent.Id;
        model.Title = guideEvent.Title;
        model.Description = guideEvent.Description;
        model.CategoryId = guideEvent.CategoryId;
        model.VenueId = guideEvent.GuideSharedVenueId ?? Guid.Empty;
        model.StartDate = localStart.Date;
        model.StartTime = localStart.TimeOfDay;
        model.IsAllDay = guideEvent.DurationMinutes == 1440;
        model.DurationMinutes = guideEvent.DurationMinutes;
        model.LocationNote = guideEvent.LocationNote;
        model.IsRecurring = guideEvent.IsRecurring;
        model.RecurrenceDays = guideEvent.RecurrenceDays;
        model.IsResubmit = guideEvent.Status is GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested;

        return View("IndividualEventForm", model);
    }

    [HttpPost("Submit/{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid eventId, IndividualEventFormViewModel model)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var guideEvent = await _dbContext.GuideEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == user.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (GuideEventStatus.Draft or GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested))
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();
        if (guideSettings == null)
        {
            SetError("Guide settings not configured.");
            return RedirectToAction(nameof(MySubmissions));
        }

        if (!ModelState.IsValid)
        {
            model.Id = eventId;
            await PopulateDropdownsAsync(model, guideSettings);
            return View("IndividualEventForm", model);
        }

        var tz = GetTimeZone(guideSettings);
        var durationMinutes = model.IsAllDay ? 1440 : model.DurationMinutes;
        var startTime = model.IsAllDay ? TimeSpan.Zero : model.StartTime;

        guideEvent.Title = model.Title;
        guideEvent.Description = model.Description;
        guideEvent.CategoryId = model.CategoryId;
        guideEvent.GuideSharedVenueId = model.VenueId;
        guideEvent.StartAt = ToInstant(model.StartDate.Add(startTime), tz);
        guideEvent.DurationMinutes = durationMinutes;
        guideEvent.LocationNote = model.LocationNote;
        guideEvent.IsRecurring = model.IsRecurring;
        guideEvent.RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null;

        guideEvent.Submit(_clock);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} updated individual event '{Title}' ({EventId})",
            user.Id, model.Title, eventId);

        SetSuccess($"Event \"{model.Title}\" resubmitted for review.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpPost("Submit/{eventId:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid eventId)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var guideEvent = await _dbContext.GuideEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == user.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (GuideEventStatus.Draft or GuideEventStatus.Pending))
        {
            SetError("This event cannot be withdrawn in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        guideEvent.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} withdrew individual event '{Title}' ({EventId})",
            user.Id, guideEvent.Title, eventId);

        SetSuccess($"Event \"{guideEvent.Title}\" withdrawn.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Schedule")]
    public async Task<IActionResult> Schedule()
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

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
            .Where(f => f.UserId == user.Id && f.GuideEvent.Status == GuideEventStatus.Approved)
            .OrderBy(f => f.GuideEvent.StartAt)
            .ToListAsync();

        var scheduleItems = favourites.Select(f =>
        {
            var e = f.GuideEvent;
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var localStart = ToLocalDateTime(e.StartAt, tz);

            var dayOffset = 0;
            if (gateOpeningDate != null)
            {
                LocalDate eventDate = tz != null
                    ? e.StartAt.InZone(tz).Date
                    : LocalDate.FromDateTime(e.StartAt.ToDateTimeUtc());
                dayOffset = Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
            }

            return new ScheduleItemViewModel
            {
                EventId = e.Id,
                Title = e.Title,
                CategoryName = e.Category.Name,
                CampName = campName,
                VenueName = e.GuideSharedVenue?.Name,
                LocationNote = e.LocationNote,
                StartAt = localStart,
                DurationMinutes = e.DurationMinutes,
                DayOffset = dayOffset,
                DayLabel = gateOpeningDate != null
                    ? gateOpeningDate.Value.PlusDays(dayOffset).ToString("ddd d MMM", null)
                    : localStart.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture),
                StartInstant = e.StartAt,
                HasConflict = false
            };
        }).ToList();

        // Detect time conflicts between favourited events
        for (var i = 0; i < scheduleItems.Count; i++)
        {
            for (var j = i + 1; j < scheduleItems.Count; j++)
            {
                var a = scheduleItems[i];
                var b = scheduleItems[j];
                var aEnd = a.StartInstant.Plus(Duration.FromMinutes(a.DurationMinutes));
                var bEnd = b.StartInstant.Plus(Duration.FromMinutes(b.DurationMinutes));

                if (a.StartInstant < bEnd && b.StartInstant < aEnd)
                {
                    a.HasConflict = true;
                    b.HasConflict = true;
                }
            }
        }

        var model = new ScheduleViewModel
        {
            TimeZoneId = guideSettings?.EventSettings?.TimeZoneId,
            DayGroups = scheduleItems
                .GroupBy(i => i.DayOffset)
                .OrderBy(g => g.Key)
                .Select(g => new ScheduleDayGroup
                {
                    DayLabel = g.First().DayLabel,
                    Items = g.OrderBy(i => i.StartAt).ToList()
                }).ToList()
        };

        return View(model);
    }

    [HttpGet("Browse")]
    public async Task<IActionResult> Browse(
        [FromQuery(Name = "days")] int[]? days, Guid? categoryId, Guid? venueId, string? q, bool favouritesOnly = false)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var gateOpeningDate = guideSettings?.EventSettings?.GateOpeningDate;
        var filterDays = days != null && days.Length > 0 ? days.ToHashSet() : null;

        // Load user's excluded categories
        var userPref = await _dbContext.UserGuidePreferences
            .FirstOrDefaultAsync(p => p.UserId == user.Id);
        var excludedSlugs = userPref != null
            ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(userPref.ExcludedCategorySlugs) ?? []
            : new List<string>();

        // Load user's favourites
        var favouriteEventIds = await _dbContext.UserEventFavourites
            .Where(f => f.UserId == user.Id)
            .Select(f => f.GuideEventId)
            .ToHashSetAsync();

        // Query approved events
        var query = _dbContext.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.GuideSharedVenue)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Where(e => e.Status == GuideEventStatus.Approved);

        // Apply category opt-out
        if (excludedSlugs.Count > 0)
            query = query.Where(e => !excludedSlugs.Contains(e.Category.Slug));

        if (categoryId.HasValue)
            query = query.Where(e => e.CategoryId == categoryId.Value);

        if (venueId.HasValue)
            query = query.Where(e => e.GuideSharedVenueId == venueId.Value);

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(e => EF.Functions.ILike(e.Title, $"%{q}%") ||
                                     EF.Functions.ILike(e.Description, $"%{q}%"));

        var events = await query.OrderBy(e => e.StartAt).ToListAsync();

        // Build browse items, expanding recurring events
        var items = new List<BrowseEventItem>();
        foreach (var e in events)
        {
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var submitterName = e.CampId == null
                ? (e.SubmitterUser?.Profile?.BurnerName ?? e.SubmitterUser?.Email)
                : null;

            var occurrences = new List<Instant> { e.StartAt };
            if (e.IsRecurring && !string.IsNullOrEmpty(e.RecurrenceDays))
            {
                occurrences.Clear();
                foreach (var offsetStr in e.RecurrenceDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(offsetStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dayOffset))
                        occurrences.Add(e.StartAt.Plus(Duration.FromDays(dayOffset)));
                }
            }

            foreach (var startInstant in occurrences)
            {
                var eventDayOffset = 0;
                if (gateOpeningDate != null)
                {
                    LocalDate eventDate = tz != null
                        ? startInstant.InZone(tz).Date
                        : LocalDate.FromDateTime(startInstant.ToDateTimeUtc());
                    eventDayOffset = Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
                }

                if (filterDays != null && !filterDays.Contains(eventDayOffset))
                    continue;

                items.Add(new BrowseEventItem
                {
                    EventId = e.Id,
                    Title = e.Title,
                    Description = e.Description,
                    CategoryName = e.Category.Name,
                    CampName = campName,
                    VenueName = e.GuideSharedVenue?.Name,
                    LocationNote = e.LocationNote,
                    StartAt = ToLocalDateTime(startInstant, tz),
                    DurationMinutes = e.DurationMinutes,
                    DayOffset = eventDayOffset,
                    IsFavourited = favouriteEventIds.Contains(e.Id),
                    SubmitterName = submitterName
                });
            }
        }

        if (favouritesOnly)
            items = items.Where(i => i.IsFavourited).ToList();

        // Build filter options
        var categories = await _dbContext.EventCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name })
            .ToListAsync();

        var venues = await _dbContext.GuideSharedVenues
            .Where(v => v.IsActive)
            .OrderBy(v => v.DisplayOrder)
            .Select(v => new VenueOptionViewModel { Id = v.Id, Name = v.Name })
            .ToListAsync();

        var eventDays = new List<EventDayOptionViewModel>();
        if (guideSettings?.EventSettings != null)
        {
            var es = guideSettings.EventSettings;
            for (var offset = 0; offset <= es.EventEndOffset; offset++)
            {
                var date = es.GateOpeningDate.PlusDays(offset);
                eventDays.Add(new EventDayOptionViewModel
                {
                    DayOffset = offset,
                    Label = date.ToString("ddd d MMM", null)
                });
            }
        }

        var model = new BrowseViewModel
        {
            TimeZoneId = guideSettings?.EventSettings?.TimeZoneId,
            FavouritedEventIds = favouriteEventIds,
            Categories = categories,
            Venues = venues,
            Days = eventDays,
            FilterDays = filterDays ?? [],
            FilterCategoryId = categoryId,
            FilterVenueId = venueId,
            SearchQuery = q,
            FavouritesOnly = favouritesOnly,
            DayGroups = items
                .GroupBy(i => i.DayOffset)
                .OrderBy(g => g.Key)
                .Select(g => new BrowseDayGroup
                {
                    DayOffset = g.Key,
                    DayLabel = gateOpeningDate != null
                        ? gateOpeningDate.Value.PlusDays(g.Key).ToString("ddd d MMM", null)
                        : g.First().StartAt.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture),
                    Items = g.OrderBy(i => i.StartAt).ToList()
                }).ToList()
        };

        return View(model);
    }

    [HttpPost("Browse/Favourite/{eventId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleFavourite(Guid eventId, [FromQuery(Name = "days")] int[]? days, Guid? categoryId, Guid? venueId, string? q, bool favouritesOnly = false)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var existing = await _dbContext.UserEventFavourites
            .FirstOrDefaultAsync(f => f.UserId == user.Id && f.GuideEventId == eventId);

        if (existing != null)
        {
            _dbContext.UserEventFavourites.Remove(existing);
        }
        else
        {
            _dbContext.UserEventFavourites.Add(new UserEventFavourite
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                GuideEventId = eventId,
                CreatedAt = _clock.GetCurrentInstant()
            });
        }

        await _dbContext.SaveChangesAsync();

        return RedirectToAction(nameof(Browse), new { days, categoryId, venueId, q, favouritesOnly });
    }

    [HttpPost("Schedule/Unfavourite/{eventId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unfavourite(Guid eventId)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var favourite = await _dbContext.UserEventFavourites
            .FirstOrDefaultAsync(f => f.UserId == user.Id && f.GuideEventId == eventId);

        if (favourite != null)
        {
            _dbContext.UserEventFavourites.Remove(favourite);
            await _dbContext.SaveChangesAsync();
            SetSuccess("Event removed from your schedule.");
        }

        return RedirectToAction(nameof(Schedule));
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<(bool IsOpen, GuideSettings? Settings)> CheckSubmissionWindowAsync()
    {
        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        if (guideSettings == null) return (false, null);

        var now = _clock.GetCurrentInstant();
        var isOpen = now >= guideSettings.SubmissionOpenAt && now <= guideSettings.SubmissionCloseAt;
        return (isOpen, guideSettings);
    }

    private async Task<IndividualEventFormViewModel> BuildFormAsync(GuideSettings guideSettings)
    {
        var model = new IndividualEventFormViewModel
        {
            TimeZoneId = guideSettings.EventSettings.TimeZoneId
        };

        await PopulateDropdownsAsync(model, guideSettings);
        return model;
    }

    private async Task PopulateDropdownsAsync(IndividualEventFormViewModel model, GuideSettings guideSettings)
    {
        model.Categories = await _dbContext.EventCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name })
            .ToListAsync();

        model.Venues = await _dbContext.GuideSharedVenues
            .Where(v => v.IsActive)
            .OrderBy(v => v.DisplayOrder)
            .Select(v => new VenueOptionViewModel { Id = v.Id, Name = v.Name })
            .ToListAsync();

        model.TimeZoneId = guideSettings.EventSettings.TimeZoneId;

        var es = guideSettings.EventSettings;
        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId);

        model.EventDays = [];
        for (var offset = 0; offset <= es.EventEndOffset; offset++)
        {
            var date = es.GateOpeningDate.PlusDays(offset);
            var dt = tz != null
                ? date.AtStartOfDayInZone(tz).ToDateTimeUnspecified()
                : new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);

            model.EventDays.Add(new EventDayOptionViewModel
            {
                DayOffset = offset,
                Label = date.ToString("ddd d MMM", null),
                Date = dt
            });
        }
    }

    private static DateTimeZone? GetTimeZone(GuideSettings guideSettings)
    {
        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId);
    }

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
    {
        if (tz == null)
            return instant.ToDateTimeUtc();
        return instant.InZone(tz).ToDateTimeUnspecified();
    }

    private static Instant ToInstant(DateTime dateTime, DateTimeZone? tz)
    {
        if (tz == null)
            return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        var local = LocalDateTime.FromDateTime(dateTime);
        return local.InZoneLeniently(tz).ToInstant();
    }
}
