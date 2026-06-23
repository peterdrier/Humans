using CsvHelper.Configuration;
using Humans.Application.Architecture;
using Humans.Application.Csv;
using Humans.Application.DTOs.Events;
using Humans.Application.Events;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Filters;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using static Humans.Web.Helpers.EventsLookupHelpers;
using static Humans.Web.Helpers.EventsTimeHelpers;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Events")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class EventsController(
    IEventService guide,
    IUserServiceRead users,
    ICampServiceRead camps,
    IAuthorizationService authorizationService,
    IClock clock,
    ILogger<EventsController> logger) : HumansCampControllerBase(users, camps, authorizationService)
{
    [HttpGet("MySubmissions")]
    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Worst-offender at HUM0031 introduction: 21 statements, cc 19.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
    public async Task<IActionResult> MySubmissions()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        var isSubmissionOpen = IsSubmissionOpen(guideSettings);

        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;

        // Personal block
        var individualEvents = await guide.GetUserSubmissionsAsync(user.Id);

        // Barrio blocks — camps the user leads/workshops
        var campSettings = await CampService.GetSettingsAsync();
        var managedCamps = (await CampService.GetCampsForYearAsync(campSettings.PublicYear))
            .Where(camp => camp.IsEventManager(user.Id))
            .ToList();

        var barrioBlocks = new List<BarrioSubmissionsBlock>();
        foreach (var camp in managedCamps)
        {
            var campEvents = await guide.GetCampSubmissionsAsync(camp.Id);
            var campName = camp.Active?.Name ?? camp.Slug;
            barrioBlocks.Add(new BarrioSubmissionsBlock
            {
                CampId = camp.Id,
                CampName = campName,
                CampSlug = camp.Slug,
                SubmittedCount = campEvents.Count,
                ApprovedCount = campEvents.Count(e => e.Status == EventStatus.Approved),
                PendingCount = campEvents.Count(e => e.Status == EventStatus.Pending),
                Events = campEvents.OrderByDescending(e => e.SubmittedAt).Select(e => new CampEventRowViewModel
                {
                    Id = e.Id,
                    Title = e.Title,
                    CategoryName = e.CategoryName,
                    StartAt = ToLocalDateTime(e.StartAt, tz),
                    DurationMinutes = e.DurationMinutes,
                    Status = e.Status,
                    PriorityRank = e.PriorityRank,
                    CanEdit = e.CanEdit,
                    CanWithdraw = e.CanWithdraw
                }).ToList()
            });
        }

        foreach (var block in barrioBlocks)
        {
            var key = $"BulkErrors_{block.CampSlug}";
            if (TempData[key] is string json)
                block.BulkUploadErrors = System.Text.Json.JsonSerializer.Deserialize<List<BulkRowError>>(json) ?? [];
        }

        var model = new MySubmissionsViewModel
        {
            IsSubmissionOpen = isSubmissionOpen,
            SubmissionOpenAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionOpenAt, tz) : null,
            SubmissionCloseAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionCloseAt, tz) : null,
            TimeZoneId = eventSettings?.TimeZoneId,
            Personal = new PersonalSubmissionsBlock
            {
                SubmittedCount = individualEvents.Count,
                ApprovedCount = individualEvents.Count(e => e.Status == EventStatus.Approved),
                PendingCount = individualEvents.Count(e => e.Status == EventStatus.Pending),
                Events = individualEvents.OrderByDescending(e => e.SubmittedAt).Select(e => new IndividualEventRowViewModel
                {
                    Id = e.Id,
                    Title = e.Title,
                    VenueName = e.VenueName ?? "—",
                    CategoryName = e.CategoryName,
                    StartAt = ToLocalDateTime(e.StartAt, tz),
                    DurationMinutes = e.DurationMinutes,
                    Status = e.Status,
                    CanEdit = e.CanEdit,
                    CanWithdraw = e.CanWithdraw
                }).ToList()
            },
            Barrios = barrioBlocks
        };

        return View(model);
    }

    [HttpGet("Submit")]
    public async Task<IActionResult> Submit()
    {
        var guideSettings = await guide.GetGuideSettingsAsync();
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var model = await BuildFormAsync(eventSettings);
        return View("IndividualEventForm", model);
    }

    [HttpPost("Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(IndividualEventFormViewModel model)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideSettings = await guide.GetGuideSettingsAsync();
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");

        if (!ModelState.IsValid)
        {
            await PopulateDropdownsAsync(model, eventSettings);
            return View("IndividualEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);
        var (startTime, durationMinutes) = Event.ResolveAllDaySchedule(
            model.IsAllDay, model.StartTime, model.DurationMinutes);

        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            CampId = null,
            GuideSharedVenueId = model.VenueId,
            SubmitterUserId = user.Id,
            CategoryId = model.CategoryId,
            Title = model.Title,
            Description = model.Description,
            LocationNote = model.LocationNote,
            Host = model.Host,
            StartAt = ToInstant(model.StartDate.Add(startTime), tz),
            DurationMinutes = durationMinutes,
            IsRecurring = model.IsRecurring,
            RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null,
            PriorityRank = 0
        };
        guideEvent.Submit(clock);

        var viewUrl = Url.Action(nameof(MySubmissions), "Events", null, Request.Scheme)!;
        await guide.SubmitEventAsync(guideEvent, viewUrl);

        logger.LogInformation("User {UserId} submitted individual event '{Title}'", user.Id, model.Title);

        SetSuccess($"Event \"{model.Title}\" submitted for review.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Submit/{eventId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid eventId)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideEvent = await guide.GetEventForModerationAsync(eventId);
        if (guideEvent is null || guideEvent.CampId != null) return NotFound();
        if (guideEvent.SubmitterUserId != user.Id && !RoleChecks.IsEventsAdmin(User))
            return Forbid();

        if (!guideEvent.CanBeEditedBySubmitter)
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await guide.GetGuideSettingsAsync();
        if (guideSettings == null)
        {
            SetError("Guide settings not configured.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var tz = GetTimeZone(eventSettings);
        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);

        var model = await BuildFormAsync(eventSettings);
        model.Id = guideEvent.Id;
        model.Title = guideEvent.Title;
        model.Description = guideEvent.Description;
        model.CategoryId = guideEvent.CategoryId;
        model.VenueId = guideEvent.GuideSharedVenueId ?? Guid.Empty;
        model.StartDate = localStart.Date;
        model.StartTime = localStart.TimeOfDay;
        model.IsAllDay = guideEvent.IsAllDay;
        model.DurationMinutes = guideEvent.DurationMinutes;
        model.LocationNote = guideEvent.LocationNote;
        model.Host = guideEvent.Host;
        model.IsRecurring = guideEvent.IsRecurring;
        model.RecurrenceDays = guideEvent.RecurrenceDays;
        model.IsResubmit = guideEvent.Status is EventStatus.Rejected or EventStatus.ResubmitRequested;

        return View("IndividualEventForm", model);
    }

    [HttpPost("Submit/{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Worst-offender at HUM0031 introduction: 42 statements, cc 17.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
    public async Task<IActionResult> Update(Guid eventId, IndividualEventFormViewModel model)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideEvent = await guide.GetEventForModerationAsync(eventId);
        if (guideEvent is null || guideEvent.CampId != null) return NotFound();
        if (guideEvent.SubmitterUserId != user.Id && !RoleChecks.IsEventsAdmin(User))
            return Forbid();

        if (!guideEvent.CanBeEditedBySubmitter)
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await guide.GetGuideSettingsAsync();
        if (guideSettings == null)
        {
            SetError("Guide settings not configured.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");

        if (!ModelState.IsValid)
        {
            model.Id = eventId;
            await PopulateDropdownsAsync(model, eventSettings);
            return View("IndividualEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);
        var (startTime, durationMinutes) = Event.ResolveAllDaySchedule(
            model.IsAllDay, model.StartTime, model.DurationMinutes);

        guideEvent.Title = model.Title;
        guideEvent.Description = model.Description;
        guideEvent.CategoryId = model.CategoryId;
        guideEvent.GuideSharedVenueId = model.VenueId;
        guideEvent.StartAt = ToInstant(model.StartDate.Add(startTime), tz);
        guideEvent.DurationMinutes = durationMinutes;
        guideEvent.LocationNote = model.LocationNote;
        guideEvent.Host = model.Host;
        guideEvent.IsRecurring = model.IsRecurring;
        guideEvent.RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null;

        try
        {
            await guide.UpdateAndResubmitAsync(guideEvent);
        }
        catch (InvalidOperationException ex) when (IsSubmitStateException(ex))
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            model.Id = eventId;
            await PopulateDropdownsAsync(model, eventSettings);
            return View("IndividualEventForm", model);
        }

        logger.LogInformation("User {UserId} updated event '{Title}' ({EventId})", user.Id, model.Title, eventId);

        SetSuccess($"Event \"{model.Title}\" resubmitted for review.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpPost("Submit/{eventId:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid eventId)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideEvent = await guide.GetUserEventAsync(eventId, user.Id);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanBeWithdrawnBySubmitter)
        {
            SetError("This event cannot be withdrawn in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        await guide.WithdrawEventAsync(guideEvent);

        logger.LogInformation("User {UserId} withdrew event '{Title}' ({EventId})", user.Id, guideEvent.Title, eventId);
        SetSuccess($"Event \"{guideEvent.Title}\" withdrawn.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Schedule")]
    public async Task<IActionResult> Schedule()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;

        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var favourites = await guide.GetFavouritesWithEventsAsync(user.Id);
        var campsById = await LoadCampsByIdAsync(CampService, gateOpeningDate?.Year);

        var scheduleItems = favourites.SelectMany(f =>
        {
            var e = f.Event;
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var campName = camp?.Active?.Name ?? camp?.Slug;

            // One line per favourited occurrence: a day-specific favourite expands
            // to that single occurrence, a whole-event favourite to all of them.
            IReadOnlyList<Instant> occurrences = gateOpeningDate.HasValue && tz != null
                ? e.GetOccurrenceInstants(gateOpeningDate.Value, tz, f.DayOffset)
                : [e.StartAt];

            return occurrences.Select(startInstant =>
            {
                var localStart = ToLocalDateTime(startInstant, tz);

                var dayOffset = 0;
                if (gateOpeningDate != null)
                {
                    LocalDate eventDate = tz != null
                        ? startInstant.InZone(tz).Date
                        : LocalDate.FromDateTime(startInstant.ToDateTimeUtc());
                    dayOffset = Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
                }

                return new ScheduleItemViewModel
                {
                    EventId = e.Id,
                    Title = e.Title,
                    CategoryName = e.CategoryName,
                    CampName = campName,
                    VenueName = e.VenueName,
                    LocationNote = e.LocationNote,
                    StartAt = localStart,
                    DurationMinutes = e.DurationMinutes,
                    DayOffset = dayOffset,
                    DayLabel = gateOpeningDate != null
                        ? gateOpeningDate.Value.PlusDays(dayOffset).ToWeekdayDayMonth()
                        : localStart.ToWeekdayDayMonth(),
                    StartInstant = startInstant,
                    FavouriteDayOffset = f.DayOffset,
                    HasConflict = false
                };
            });
        }).OrderBy(i => i.StartInstant).ToList();

        // Detect time conflicts
        foreach (var index in EventConflictDetector.FindConflictingIndexes(
                     scheduleItems, i => i.StartInstant, i => i.DurationMinutes))
        {
            scheduleItems[index].HasConflict = true;
        }

        var model = new ScheduleViewModel
        {
            TimeZoneId = eventSettings?.TimeZoneId,
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
    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Worst-offender at HUM0031 introduction: 38 statements, cc 23.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
    public async Task<IActionResult> Browse(
        [FromQuery(Name = "days")] int[]? days, Guid? categoryId, Guid? venueId, string? q, bool favouritesOnly = false)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;

        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var filterDays = days != null && days.Length > 0 ? days.ToHashSet() : null;

        var excludedSlugs = await guide.GetExcludedCategorySlugsAsync(user.Id);
        var favouriteDaysByEventId = (await guide.GetFavouritesWithEventsAsync(user.Id))
            .ToLookup(f => f.GuideEventId, f => f.DayOffset);
        var events = await guide.GetApprovedEventsAsync(null, venueId, categoryId, q, excludedSlugs);

        var campsById = await LoadCampsByIdAsync(CampService, gateOpeningDate?.Year);
        var individualSubmitterIds = events.Where(e => e.CampId == null).Select(e => e.SubmitterUserId).Distinct();
        var submitterInfoById = await LoadSubmittersAsync(UserService, individualSubmitterIds);

        var items = new List<BrowseEventItem>();
        foreach (var e in events)
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var campName = camp?.Active?.Name ?? camp?.Slug;
            var submitterName = e.CampId == null
                ? submitterInfoById.GetValueOrDefault(e.SubmitterUserId)?.BurnerName
                : null;

            foreach (var startInstant in gateOpeningDate.HasValue && tz != null ? e.GetOccurrenceInstants(gateOpeningDate.Value, tz) : (IReadOnlyList<Instant>)[e.StartAt])
            {
                var eventDayOffset = 0;
                if (gateOpeningDate != null)
                {
                    LocalDate eventDate = tz != null
                        ? startInstant.InZone(tz).Date
                        : LocalDate.FromDateTime(startInstant.ToDateTimeUtc());
                    eventDayOffset = Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
                }

                if (filterDays != null && !filterDays.Contains(eventDayOffset)) continue;

                // Hearts on recurring-event cards favourite that day's occurrence;
                // non-recurring cards (and unexpanded ones) favourite the whole event.
                var favouriteDayOffset = e.IsRecurring && gateOpeningDate.HasValue && tz != null
                    ? eventDayOffset
                    : (int?)null;

                items.Add(new BrowseEventItem
                {
                    EventId = e.Id,
                    Title = e.Title,
                    Description = e.Description,
                    CategoryName = e.CategoryName,
                    CampName = campName,
                    VenueName = e.VenueName,
                    LocationNote = e.LocationNote,
                    StartAt = ToLocalDateTime(startInstant, tz),
                    DurationMinutes = e.DurationMinutes,
                    DayOffset = eventDayOffset,
                    FavouriteDayOffset = favouriteDayOffset,
                    IsFavourited = favouriteDaysByEventId[e.Id].Any(d => d == null || d == favouriteDayOffset),
                    SubmitterName = submitterName,
                    DisplayHost = e.CampId == null
                        ? (e.Host ?? submitterName)
                        : e.Host
                });
            }
        }

        if (favouritesOnly)
            items = items.Where(i => i.IsFavourited).ToList();

        var categories = await guide.GetActiveCategoriesAsync();
        var venues = await guide.GetActiveVenuesAsync();

        var eventDays = new List<EventDayOptionViewModel>();
        if (eventSettings != null)
        {
            for (var offset = 0; offset <= eventSettings.EventEndOffset; offset++)
            {
                var date = eventSettings.GateOpeningDate.PlusDays(offset);
                eventDays.Add(new EventDayOptionViewModel
                {
                    DayOffset = offset,
                    Label = date.ToWeekdayDayMonth()
                });
            }
        }

        var model = new BrowseViewModel
        {
            TimeZoneId = eventSettings?.TimeZoneId,
            Categories = categories.Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList(),
            Venues = venues.Select(v => new VenueOptionViewModel { Id = v.Id, Name = v.Name }).ToList(),
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
                        ? gateOpeningDate.Value.PlusDays(g.Key).ToWeekdayDayMonth()
                        : g.First().StartAt.ToWeekdayDayMonth(),
                    Items = g.OrderBy(i => i.StartAt).ToList()
                }).ToList()
        };

        return View(model);
    }

    private bool IsSubmissionOpen(EventGuideSettingsView? settings) =>
        settings?.IsSubmissionOpenAt(clock.GetCurrentInstant()) ?? false;

    private async Task<IndividualEventFormViewModel> BuildFormAsync(BurnSettingsInfo burn)
    {
        var model = new IndividualEventFormViewModel
        {
            TimeZoneId = burn.TimeZoneId
        };
        await PopulateDropdownsAsync(model, burn);
        return model;
    }

    private async Task PopulateDropdownsAsync(IndividualEventFormViewModel model, BurnSettingsInfo burn)
    {
        var categories = await guide.GetActiveCategoriesAsync();
        var venues = await guide.GetActiveVenuesAsync();

        model.Categories = categories.Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList();
        model.Venues = venues.Select(v => new VenueOptionViewModel { Id = v.Id, Name = v.Name }).ToList();
        model.TimeZoneId = burn.TimeZoneId;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);

        model.EventDays = [];
        for (var offset = 0; offset <= burn.EventEndOffset; offset++)
        {
            var date = burn.GateOpeningDate.PlusDays(offset);
            var dt = tz != null
                ? date.AtStartOfDayInZone(tz).ToDateTimeUnspecified()
                : new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);

            model.EventDays.Add(new EventDayOptionViewModel
            {
                DayOffset = offset,
                Label = date.ToWeekdayDayMonth(),
                Date = dt
            });
        }
    }

    // ─── Barrio event actions (replaces /Barrios/{slug}/Events/*) ────────────

    [HttpGet("Barrio/{slug}/Submit")]
    public async Task<IActionResult> BarrioSubmit(string slug)
    {
        var (error, _, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync();
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var model = await BuildBarrioFormAsync(slug, camp, eventSettings);
        return View("BarrioEventForm", model);
    }

    [HttpPost("Barrio/{slug}/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BarrioCreate(string slug, CampEventFormViewModel model)
    {
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync();
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");

        if (!ModelState.IsValid)
        {
            model.CampId = camp.Id;
            model.CampName = ResolveCampDisplayName(camp);
            model.CampSlug = slug;
            await PopulateBarrioDropdownsAsync(model, eventSettings);
            return View("BarrioEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            SubmitterUserId = user.Id,
            CategoryId = model.CategoryId,
            Title = model.Title,
            Description = model.Description,
            LocationNote = model.LocationNote,
            Host = model.Host,
            StartAt = ToInstant(model.StartDate.Add(model.StartTime), tz),
            DurationMinutes = model.DurationMinutes,
            IsRecurring = model.IsRecurring,
            RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null,
            PriorityRank = model.PriorityRank
        };
        guideEvent.Submit(clock);

        var viewUrl = Url.Action(nameof(MySubmissions), "Events", null, Request.Scheme)!;
        await guide.SubmitEventAsync(guideEvent, viewUrl);
        logger.LogInformation("User {UserId} submitted barrio event '{Title}' for camp {CampId}", user.Id, model.Title, camp.Id);

        SetSuccess($"Event \"{model.Title}\" submitted for review.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Barrio/{slug}/{eventId:guid}/Edit")]
    public async Task<IActionResult> BarrioEdit(string slug, Guid eventId)
    {
        var (error, _, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanBeEditedBySubmitter)
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await guide.GetGuideSettingsAsync()
            ?? throw new InvalidOperationException("Guide settings not configured.");
        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var tz = GetTimeZone(eventSettings);
        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);

        var model = await BuildBarrioFormAsync(slug, camp, eventSettings);
        model.Id = guideEvent.Id;
        model.Title = guideEvent.Title;
        model.Description = guideEvent.Description;
        model.CategoryId = guideEvent.CategoryId;
        model.StartDate = localStart.Date;
        model.StartTime = localStart.TimeOfDay;
        model.DurationMinutes = guideEvent.DurationMinutes;
        model.LocationNote = guideEvent.LocationNote;
        model.Host = guideEvent.Host;
        model.IsRecurring = guideEvent.IsRecurring;
        model.RecurrenceDays = guideEvent.RecurrenceDays;
        model.PriorityRank = guideEvent.PriorityRank;
        model.IsResubmit = guideEvent.Status is EventStatus.Rejected or EventStatus.ResubmitRequested;

        return View("BarrioEventForm", model);
    }

    [HttpPost("Barrio/{slug}/{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Worst-offender at HUM0031 introduction: 41 statements, cc 11.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
    public async Task<IActionResult> BarrioUpdate(string slug, Guid eventId, CampEventFormViewModel model)
    {
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanBeEditedBySubmitter)
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await guide.GetGuideSettingsAsync()
            ?? throw new InvalidOperationException("Guide settings not configured.");
        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");

        if (!ModelState.IsValid)
        {
            model.Id = eventId;
            model.CampId = camp.Id;
            model.CampName = ResolveCampDisplayName(camp);
            model.CampSlug = slug;
            await PopulateBarrioDropdownsAsync(model, eventSettings);
            return View("BarrioEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);
        guideEvent.Title = model.Title;
        guideEvent.Description = model.Description;
        guideEvent.CategoryId = model.CategoryId;
        guideEvent.StartAt = ToInstant(model.StartDate.Add(model.StartTime), tz);
        guideEvent.DurationMinutes = model.DurationMinutes;
        guideEvent.LocationNote = model.LocationNote;
        guideEvent.Host = model.Host;
        guideEvent.IsRecurring = model.IsRecurring;
        guideEvent.RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null;
        guideEvent.PriorityRank = model.PriorityRank;

        try
        {
            await guide.UpdateAndResubmitAsync(guideEvent);
        }
        catch (InvalidOperationException ex) when (IsSubmitStateException(ex))
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            model.Id = eventId;
            model.CampId = camp.Id;
            model.CampName = ResolveCampDisplayName(camp);
            model.CampSlug = slug;
            await PopulateBarrioDropdownsAsync(model, eventSettings);
            return View("BarrioEventForm", model);
        }

        logger.LogInformation("User {UserId} updated barrio event '{Title}' ({EventId})", user.Id, model.Title, eventId);

        SetSuccess($"Event \"{model.Title}\" resubmitted for review.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpPost("Barrio/{slug}/{eventId:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BarrioWithdraw(string slug, Guid eventId)
    {
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanBeWithdrawnBySubmitter)
        {
            SetError("This event cannot be withdrawn in its current state.");
            return RedirectToAction(nameof(MySubmissions));
        }

        await guide.WithdrawEventAsync(guideEvent);
        logger.LogInformation("User {UserId} withdrew barrio event '{Title}' ({EventId})", user.Id, guideEvent.Title, eventId);

        SetSuccess($"Event \"{guideEvent.Title}\" withdrawn.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Barrio/{slug}/BulkUpload/Template")]
    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Worst-offender at HUM0031 introduction: 60 statements, cc 14.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
    public async Task<IActionResult> BulkUploadTemplate(string slug)
    {
        var (error, _, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = await LoadBurnSettingsAsync(guideSettings);

        var campEvents = await guide.GetCampSubmissionsAsync(camp.Id);
        var categories = await guide.GetActiveCategoriesAsync();
        var campName = ResolveCampDisplayName(camp);

        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;
        LocalDate? gateDate = eventSettings?.GateOpeningDate;

        var categoryNames = string.Join(", ", categories.Select(c => c.Name));

        string[] banner =
        [
            " ─────────────────────────────────────────────────────────────────────────────",
            " ELSEWHERE EVENT GUIDE — Bulk Upload Template",
            " ─────────────────────────────────────────────────────────────────────────────",
            "",
            " HOW TO USE",
            "   1. Fill in new rows leaving Id blank — a new event will be created.",
            "   2. Existing rows already have an Id filled in. You may edit their fields,",
            "      but DO NOT change or delete the Id — that is how we match the event.",
            "      Changing an Id will cause the upload to fail.",
            "   3. To leave an existing event unchanged, keep its row as-is.",
            "      Events not present in the CSV are left untouched.",
            "   4. Save as CSV (UTF-8) before uploading. Columns may be in any order and",
            "      extra columns are ignored — match the column names, not the layout.",
            "      In Excel:   File → Save As → CSV UTF-8 (Comma delimited)",
            "      In Numbers: File → Export To → CSV",
            "",
            " FIELDS",
            "   Id             Leave empty for new events. Do not edit for existing ones.",
            "   Barrio         Informational only — shows which camp this file belongs to. Ignored on upload.",
            "   Status         Informational only — shows the current event status. Ignored on upload.",
            "                  If you upload a row without changing any fields, the status is kept as-is.",
            "                  If you edit fields on an existing event, it will be re-queued for moderation.",
            "   Category       Must match exactly one of the valid categories listed below.",
            "   Date           Format: yyyy-MM-dd  (e.g. 2026-07-08)",
            "   StartTime      Format: HH:mm       (e.g. 09:30)",
            "   DurationMinutes  Integer, 15–480, in 15-minute increments (e.g. 15, 30, 45, 60, 90, 120...).",
            "   IsRecurring    true or false.",
            "   RecurrenceDays  Only used when IsRecurring is true.",
            "                  Space-separated day names: Mon Tue Wed Thu Fri Sat Sun",
            "                  Example: Mon Wed Fri means the event repeats on those days.",
            "",
            " VALID CATEGORIES",
            $"   {categoryNames}",
            "",
            " ─────────────────────────────────────────────────────────────────────────────",
        ];

        var nonWithdrawn = campEvents
            .Where(e => e.Status != EventStatus.Withdrawn)
            .OrderByDescending(e => e.SubmittedAt)
            .ToList();

        var records = new List<BulkEventCsvRecord>();
        foreach (var e in nonWithdrawn)
        {
            var localDt = ToLocalDateTime(e.StartAt, tz);
            var recDays = e.IsRecurring && !string.IsNullOrEmpty(e.RecurrenceDays) && gateDate.HasValue
                ? EventRecurrenceDays.OffsetsToDisplayDays(e.RecurrenceDays, gateDate.Value)
                : string.Empty;

            records.Add(new BulkEventCsvRecord
            {
                Id = e.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                Barrio = campName,
                Status = e.Status.ToString(),
                Title = e.Title,
                Description = e.Description,
                Category = e.CategoryName,
                Date = localDt.ToInvariantDate(),
                StartTime = localDt.ToInvariantTime(),
                DurationMinutes = e.DurationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LocationNote = e.LocationNote ?? string.Empty,
                Host = e.Host ?? string.Empty,
                IsRecurring = e.IsRecurring ? "true" : "false",
                RecurrenceDays = recDays,
                PriorityRank = e.PriorityRank.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        if (nonWithdrawn.Count == 0)
        {
            var exampleDate = gateDate.HasValue
                ? gateDate.Value.ToInvariantDate()
                : clock.GetCurrentInstant().InZone(tz ?? DateTimeZone.Utc).Date.ToInvariantDate();
            records.Add(new BulkEventCsvRecord
            {
                Barrio = campName,
                Title = "Example Event",
                Description = "Describe your event here.",
                Category = categories.FirstOrDefault()?.Name ?? "Workshop",
                Date = exampleDate,
                StartTime = "12:00",
                DurationMinutes = "60",
                IsRecurring = "false",
                PriorityRank = "1",
            });
        }

        var bytes = HumansCsv.WriteBytes(
            csv =>
            {
                csv.Context.RegisterClassMap<BulkEventCsvRecordMap>();
                foreach (var line in banner)
                {
                    csv.WriteComment(line);
                    csv.NextRecord();
                }
                csv.WriteRecords(records);
            },
            // Round-trip data file, not a spreadsheet report: injection escaping
            // would prepend apostrophes that come back as data on re-upload,
            // dirtying rows the user never touched.
            config => config.InjectionOptions = InjectionOptions.None);
        return File(bytes, "text/csv", $"{slug}-events.csv");
    }

    [HttpPost("Barrio/{slug}/BulkUpload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(1 * 1024 * 1024)]
    public async Task<IActionResult> BulkUploadImport(string slug, IFormFile? file)
    {
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync();
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(MySubmissions));
        }

        if (file == null || file.Length == 0)
        {
            SetError("Please select a CSV file to upload.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var tz = GetTimeZone(eventSettings)
            ?? throw new InvalidOperationException("Event timezone not configured.");

        List<BulkCsvRow> rows;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8);
            rows = BulkEventCsvParser.Parse(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            logger.LogWarning("Bulk CSV parse failed for camp slug {Slug}: {Message}", slug, ex.Message);
            SetError($"Could not parse CSV: {ex.Message}");
            return RedirectToAction(nameof(MySubmissions));
        }

        if (rows.Count == 0)
        {
            SetError("The CSV had no event rows. Add at least one row below the header and try again.");
            return RedirectToAction(nameof(MySubmissions));
        }

        var result = await guide.BulkImportAsync(
            camp.Id, user.Id, rows,
            eventSettings.GateOpeningDate, eventSettings.EventEndOffset, tz);

        if (result.HasErrors)
        {
            var viewErrors = result.Errors
                .Select(e => new BulkRowError { RowNumber = e.RowNumber, Title = e.Title, Errors = e.Errors.ToList() })
                .ToList();
            TempData[$"BulkErrors_{slug}"] = System.Text.Json.JsonSerializer.Serialize(viewErrors);
            return RedirectToAction(nameof(MySubmissions));
        }

        logger.LogInformation(
            "Bulk upload by user {UserId} for camp {CampId}: {Created} created, {Updated} updated.",
            user.Id, camp.Id, result.CreatedCount, result.UpdatedCount);
        SetSuccess($"Bulk upload complete — {result.CreatedCount} created, {result.UpdatedCount} updated.");
        return RedirectToAction(nameof(MySubmissions));
    }

    private static string ResolveCampDisplayName(CampInfo camp) =>
        camp.Active?.Name ?? camp.Slug;

    private async Task<CampEventFormViewModel> BuildBarrioFormAsync(string slug, CampInfo camp, BurnSettingsInfo burn)
    {
        var model = new CampEventFormViewModel
        {
            CampId = camp.Id,
            CampName = ResolveCampDisplayName(camp),
            CampSlug = slug,
            TimeZoneId = burn.TimeZoneId
        };
        await PopulateBarrioDropdownsAsync(model, burn);
        return model;
    }

    private async Task PopulateBarrioDropdownsAsync(CampEventFormViewModel model, BurnSettingsInfo burn)
    {
        var categories = await guide.GetActiveCategoriesAsync();
        model.Categories = categories.Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList();
        model.TimeZoneId = burn.TimeZoneId;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
        model.EventDays = [];
        for (var offset = 0; offset <= burn.EventEndOffset; offset++)
        {
            var date = burn.GateOpeningDate.PlusDays(offset);
            var dt = tz != null
                ? date.AtStartOfDayInZone(tz).ToDateTimeUnspecified()
                : new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);
            model.EventDays.Add(new EventDayOptionViewModel
            {
                DayOffset = offset,
                Label = date.ToWeekdayDayMonth(),
                Date = dt
            });
        }
    }

    private async Task<BurnSettingsInfo?> LoadBurnSettingsAsync(EventGuideSettingsView? guideSettings)
    {
        if (guideSettings == null) return null;
        return await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId);
    }

    internal static bool IsSubmitStateException(InvalidOperationException ex) =>
        ex.Message.StartsWith("Cannot submit event in ", StringComparison.Ordinal)
        && ex.Message.EndsWith(" state", StringComparison.Ordinal);

}
