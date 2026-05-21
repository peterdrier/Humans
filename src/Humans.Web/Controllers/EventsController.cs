using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
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
    IUserService users,
    ICampService camps,
    IAuthorizationService authorizationService,
    IClock clock,
    IEmailService emailService,
    ILogger<EventsController> logger) : HumansCampControllerBase(users, camps, authorizationService)
{
    [HttpGet("MySubmissions")]
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
        var managedCamps = await CampService.GetEventManagedCampsAsync(user.Id, campSettings.PublicYear);

        var barrioBlocks = new List<BarrioSubmissionsBlock>();
        foreach (var camp in managedCamps)
        {
            var campEvents = await guide.GetCampSubmissionsAsync(camp.Id);
            var campName = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name ?? camp.Slug;
            barrioBlocks.Add(new BarrioSubmissionsBlock
            {
                CampId = camp.Id,
                CampName = campName,
                CampSlug = camp.Slug,
                SubmittedCount = campEvents.Count,
                ApprovedCount = campEvents.Count(e => e.Status == EventStatus.Approved),
                PendingCount = campEvents.Count(e => e.Status == EventStatus.Pending),
                Events = campEvents.Select(e => new CampEventRowViewModel
                {
                    Id = e.Id,
                    Title = e.Title,
                    CategoryName = e.Category.Name,
                    StartAt = ToLocalDateTime(e.StartAt, tz),
                    DurationMinutes = e.DurationMinutes,
                    Status = e.Status,
                    PriorityRank = e.PriorityRank,
                    CanEdit = e.Status is EventStatus.Rejected or EventStatus.ResubmitRequested or EventStatus.Pending,
                    CanWithdraw = e.Status is EventStatus.Pending or EventStatus.Approved
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
                Events = individualEvents.Select(e => new IndividualEventRowViewModel
                {
                    Id = e.Id,
                    Title = e.Title,
                    VenueName = e.EventVenue?.Name ?? "—",
                    CategoryName = e.Category.Name,
                    StartAt = ToLocalDateTime(e.StartAt, tz),
                    DurationMinutes = e.DurationMinutes,
                    Status = e.Status,
                    CanEdit = e.Status is EventStatus.Draft or EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested,
                    CanWithdraw = e.Status is EventStatus.Draft or EventStatus.Pending or EventStatus.Approved
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
        var model = await BuildFormAsync(guideSettings!, eventSettings);
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
        var durationMinutes = model.IsAllDay ? 1440 : model.DurationMinutes;
        var startTime = model.IsAllDay ? TimeSpan.Zero : model.StartTime;

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

        await guide.SubmitEventAsync(guideEvent);

        logger.LogInformation("User {UserId} submitted individual event '{Title}'", user.Id, model.Title);

        var userEmail = user.Email;
        if (userEmail != null)
        {
            var userInfo = await UserService.GetUserInfoAsync(user.Id);
            var viewUrl = Url.Action(nameof(MySubmissions), "Events", null, Request.Scheme)!;
            await emailService.SendEventLifecycleNotificationAsync(
                new EventLifecycleNotification(
                    NewStatus: EventStatus.Pending,
                    UserName: userInfo?.BurnerName ?? userEmail,
                    EventTitle: model.Title,
                    ActionUrl: viewUrl),
                userEmail);
        }

        SetSuccess($"Event \"{model.Title}\" submitted for review.");
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Submit/{eventId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid eventId)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideEvent = await guide.GetUserEventAsync(eventId, user.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (EventStatus.Draft or EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested))
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

        var model = await BuildFormAsync(guideSettings, eventSettings);
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
        model.Host = guideEvent.Host;
        model.IsRecurring = guideEvent.IsRecurring;
        model.RecurrenceDays = guideEvent.RecurrenceDays;
        model.IsResubmit = guideEvent.Status is EventStatus.Rejected or EventStatus.ResubmitRequested;

        return View("IndividualEventForm", model);
    }

    [HttpPost("Submit/{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid eventId, IndividualEventFormViewModel model)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        var guideEvent = await guide.GetUserEventAsync(eventId, user.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (EventStatus.Draft or EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested))
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
        var durationMinutes = model.IsAllDay ? 1440 : model.DurationMinutes;
        var startTime = model.IsAllDay ? TimeSpan.Zero : model.StartTime;

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

        if (guideEvent.Status is not (EventStatus.Draft or EventStatus.Pending or EventStatus.Approved))
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

        var scheduleItems = favourites.Select(f =>
        {
            var e = f.Event;
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var seasonName = camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name;
            var campName = seasonName ?? camp?.Slug;
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
                VenueName = e.EventVenue?.Name,
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

        // Detect time conflicts
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
        var favouriteEventIds = await guide.GetFavouriteEventIdsAsync(user.Id);
        var events = await guide.GetApprovedEventsAsync(null, venueId, categoryId, q, excludedSlugs);

        var campsById = await LoadCampsByIdAsync(CampService, gateOpeningDate?.Year);
        var individualSubmitterIds = events.Where(e => e.CampId == null).Select(e => e.SubmitterUserId).Distinct();
        var submitterInfoById = await LoadSubmittersAsync(UserService, individualSubmitterIds);

        var items = new List<BrowseEventItem>();
        foreach (var e in events)
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var seasonName = camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name;
            var campName = seasonName ?? camp?.Slug;
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

                items.Add(new BrowseEventItem
                {
                    EventId = e.Id,
                    Title = e.Title,
                    Description = e.Description,
                    CategoryName = e.Category.Name,
                    CampName = campName,
                    VenueName = e.EventVenue?.Name,
                    LocationNote = e.LocationNote,
                    StartAt = ToLocalDateTime(startInstant, tz),
                    DurationMinutes = e.DurationMinutes,
                    DayOffset = eventDayOffset,
                    IsFavourited = favouriteEventIds.Contains(e.Id),
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
                    Label = date.ToString("ddd d MMM", null)
                });
            }
        }

        var model = new BrowseViewModel
        {
            TimeZoneId = eventSettings?.TimeZoneId,
            FavouritedEventIds = favouriteEventIds,
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
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        await guide.ToggleFavouriteAsync(user.Id, eventId);
        return RedirectToAction(nameof(Browse), new { days, categoryId, venueId, q, favouritesOnly });
    }

    [HttpPost("Schedule/Unfavourite/{eventId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unfavourite(Guid eventId)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user == null) return Challenge();

        if (await guide.RemoveFavouriteAsync(user.Id, eventId))
            SetSuccess("Event removed from your schedule.");

        return RedirectToAction(nameof(Schedule));
    }

    private bool IsSubmissionOpen(EventGuideSettings? settings) =>
        settings?.IsSubmissionOpenAt(clock.GetCurrentInstant()) ?? false;

    private async Task<IndividualEventFormViewModel> BuildFormAsync(EventGuideSettings guideSettings, BurnSettingsInfo burn)
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
                Label = date.ToString("ddd d MMM", null),
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

        await guide.SubmitEventAsync(guideEvent);
        logger.LogInformation("User {UserId} submitted barrio event '{Title}' for camp {CampId}", user.Id, model.Title, camp.Id);

        var userEmail = user.Email;
        if (userEmail != null)
        {
            var userInfo = await UserService.GetUserInfoAsync(user.Id);
            var viewUrl = Url.Action(nameof(MySubmissions), "Events", null, Request.Scheme)!;
            await emailService.SendEventLifecycleNotificationAsync(
                new EventLifecycleNotification(EventStatus.Pending, userInfo?.BurnerName ?? userEmail, model.Title, viewUrl),
                userEmail);
        }

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

        if (guideEvent.Status is not (EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested))
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
    public async Task<IActionResult> BarrioUpdate(string slug, Guid eventId, CampEventFormViewModel model)
    {
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested))
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

        if (guideEvent.Status is not (EventStatus.Pending or EventStatus.Approved))
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

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# ─────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine("# ELSEWHERE EVENT GUIDE — Bulk Upload Template");
        sb.AppendLine("# ─────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine("#");
        sb.AppendLine("# HOW TO USE");
        sb.AppendLine("#   1. Fill in new rows leaving Id blank — a new event will be created.");
        sb.AppendLine("#   2. Existing rows already have an Id filled in. You may edit their fields,");
        sb.AppendLine("#      but DO NOT change or delete the Id — that is how we match the event.");
        sb.AppendLine("#      Changing an Id will cause the upload to fail.");
        sb.AppendLine("#   3. To leave an existing event unchanged, keep its row as-is.");
        sb.AppendLine("#      Events not present in the CSV are left untouched.");
        sb.AppendLine("#   4. Save as CSV (comma-separated, UTF-8) before uploading.");
        sb.AppendLine("#      In Excel:   File → Save As → CSV UTF-8 (Comma delimited)");
        sb.AppendLine("#      In Numbers: File → Export To → CSV");
        sb.AppendLine("#");
        sb.AppendLine("# FIELDS");
        sb.AppendLine("#   Id             Leave empty for new events. Do not edit for existing ones.");
        sb.AppendLine("#   Barrio         Informational only — shows which camp this file belongs to. Ignored on upload.");
        sb.AppendLine("#   Status         Informational only — shows the current event status. Ignored on upload.");
        sb.AppendLine("#                  If you upload a row without changing any fields, the status is kept as-is.");
        sb.AppendLine("#                  If you edit fields on an existing event, it will be re-queued for moderation.");
        sb.AppendLine("#   Category       Must match exactly one of the valid categories listed below.");
        sb.AppendLine("#   Date           Format: yyyy-MM-dd  (e.g. 2026-07-08)");
        sb.AppendLine("#   StartTime      Format: HH:mm       (e.g. 09:30)");
        sb.AppendLine("#   DurationMinutes  Integer, 15–480, in 15-minute increments (e.g. 15, 30, 45, 60, 90, 120...).");
        sb.AppendLine("#   IsRecurring    true or false.");
        sb.AppendLine("#   RecurrenceDays  Only used when IsRecurring is true.");
        sb.AppendLine("#                  Space-separated day names: Mon Tue Wed Thu Fri Sat Sun");
        sb.AppendLine("#                  Example: Mon Wed Fri means the event repeats on those days.");
        sb.AppendLine("#");
        sb.AppendLine($"# VALID CATEGORIES");
        sb.AppendLine($"#   {categoryNames}");
        sb.AppendLine("#");
        sb.AppendLine("# ─────────────────────────────────────────────────────────────────────────────");

        sb.AppendLine("\"Id\",\"Barrio\",\"Status\",\"Title\",\"Description\",\"Category\",\"Date\",\"StartTime\",\"DurationMinutes\",\"LocationNote\",\"Host\",\"IsRecurring\",\"RecurrenceDays\",\"PriorityRank\"");

        var nonWithdrawn = campEvents.Where(e => e.Status != Domain.Enums.EventStatus.Withdrawn);
        foreach (var e in nonWithdrawn)
        {
            var localDt = ToLocalDateTime(e.StartAt, tz);
            var date = localDt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var time = localDt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var recDays = e.IsRecurring && !string.IsNullOrEmpty(e.RecurrenceDays) && gateDate.HasValue
                ? OffsetsToDisplayDays(e.RecurrenceDays, gateDate.Value)
                : string.Empty;

            sb.AppendLine(string.Join(",",
                CsvField(e.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture)),
                CsvField(campName),
                CsvField(e.Status.ToString()),
                CsvField(e.Title),
                CsvField(e.Description),
                CsvField(e.Category.Name),
                CsvField(date),
                CsvField(time),
                CsvField(e.DurationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                CsvField(e.LocationNote ?? string.Empty),
                CsvField(e.Host ?? string.Empty),
                CsvField(e.IsRecurring ? "true" : "false"),
                CsvField(recDays),
                CsvField(e.PriorityRank.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"{slug}-events.csv");
    }

    [HttpPost("Barrio/{slug}/BulkUpload")]
    [ValidateAntiForgeryToken]
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
        var tz = GetTimeZone(eventSettings);
        var categories = await guide.GetActiveCategoriesAsync();
        var existingEvents = await guide.GetCampSubmissionsAsync(camp.Id);

        List<BulkCsvRow> rows;
        try
        {
            using var reader = new System.IO.StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8);
            rows = ParseCsvRows(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            SetError($"Could not parse CSV: {ex.Message}");
            return RedirectToAction(nameof(MySubmissions));
        }

        var rowErrors = ValidateBulkRows(rows, categories, existingEvents, eventSettings.GateOpeningDate);
        if (rowErrors.Count > 0)
        {
            TempData[$"BulkErrors_{slug}"] = System.Text.Json.JsonSerializer.Serialize(rowErrors);
            return RedirectToAction(nameof(MySubmissions));
        }

        var campName = ResolveCampDisplayName(camp);
        foreach (var row in rows)
        {
            if (row.Id.HasValue)
            {
                var existing = existingEvents.First(e => e.Id == row.Id.Value);
                var newInstant = ToInstant(DateTime.Parse($"{row.Date} {row.StartTime}", System.Globalization.CultureInfo.InvariantCulture), tz);
                var newRecDays = row.IsRecurring && !string.IsNullOrEmpty(row.RecurrenceDays)
                    ? DisplayDaysToOffsets(row.RecurrenceDays, eventSettings.GateOpeningDate, eventSettings.EventEndOffset)
                    : null;

                var changed =
                    !string.Equals(existing.Title, row.Title, StringComparison.Ordinal) ||
                    !string.Equals(existing.Description, row.Description, StringComparison.Ordinal) ||
                    !string.Equals(existing.Category.Name, row.Category, StringComparison.OrdinalIgnoreCase) ||
                    existing.StartAt != newInstant ||
                    existing.DurationMinutes != row.DurationMinutes ||
                    !string.Equals(existing.LocationNote ?? string.Empty, row.LocationNote ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(existing.Host ?? string.Empty, row.Host ?? string.Empty, StringComparison.Ordinal) ||
                    existing.IsRecurring != row.IsRecurring ||
                    !string.Equals(existing.RecurrenceDays ?? string.Empty, newRecDays ?? string.Empty, StringComparison.Ordinal) ||
                    existing.PriorityRank != row.PriorityRank;

                if (!changed) continue;

                var matchedCategory = categories.First(c => string.Equals(c.Name, row.Category, StringComparison.OrdinalIgnoreCase));
                existing.Title = row.Title;
                existing.Description = row.Description;
                existing.CategoryId = matchedCategory.Id;
                existing.StartAt = newInstant;
                existing.DurationMinutes = row.DurationMinutes;
                existing.LocationNote = string.IsNullOrEmpty(row.LocationNote) ? null : row.LocationNote;
                existing.Host = string.IsNullOrEmpty(row.Host) ? null : row.Host;
                existing.IsRecurring = row.IsRecurring;
                existing.RecurrenceDays = row.IsRecurring ? newRecDays : null;
                existing.PriorityRank = row.PriorityRank;

                if (existing.Status == Domain.Enums.EventStatus.Draft)
                {
                    existing.Submit(clock);
                    await guide.SubmitEventAsync(existing);
                }
                else
                {
                    await guide.UpdateAndResubmitAsync(existing);
                }
                logger.LogInformation("Bulk upload: user {UserId} updated event '{Title}' ({EventId})", user.Id, existing.Title, existing.Id);
            }
            else
            {
                var matchedCategory = categories.First(c => string.Equals(c.Name, row.Category, StringComparison.OrdinalIgnoreCase));
                var recDays = row.IsRecurring && !string.IsNullOrEmpty(row.RecurrenceDays)
                    ? DisplayDaysToOffsets(row.RecurrenceDays, eventSettings.GateOpeningDate, eventSettings.EventEndOffset)
                    : null;
                var newEvent = new Domain.Entities.Event
                {
                    Id = Guid.NewGuid(),
                    CampId = camp.Id,
                    SubmitterUserId = user.Id,
                    CategoryId = matchedCategory.Id,
                    Title = row.Title,
                    Description = row.Description,
                    LocationNote = string.IsNullOrEmpty(row.LocationNote) ? null : row.LocationNote,
                    Host = string.IsNullOrEmpty(row.Host) ? null : row.Host,
                    StartAt = ToInstant(DateTime.Parse($"{row.Date} {row.StartTime}", System.Globalization.CultureInfo.InvariantCulture), tz),
                    DurationMinutes = row.DurationMinutes,
                    IsRecurring = row.IsRecurring,
                    RecurrenceDays = recDays,
                    PriorityRank = row.PriorityRank
                };
                newEvent.Submit(clock);
                await guide.SubmitEventAsync(newEvent);
                logger.LogInformation("Bulk upload: user {UserId} submitted new event '{Title}' for camp {CampId}", user.Id, row.Title, camp.Id);

                var userEmail = user.Email;
                if (userEmail != null)
                {
                    var userInfo = await UserService.GetUserInfoAsync(user.Id);
                    var viewUrl = Url.Action(nameof(MySubmissions), "Events", null, Request.Scheme)!;
                    await emailService.SendEventLifecycleNotificationAsync(
                        new EventLifecycleNotification(Domain.Enums.EventStatus.Pending, userInfo?.BurnerName ?? userEmail, row.Title, viewUrl),
                        userEmail);
                }
            }
        }

        SetSuccess("Bulk upload complete.");
        return RedirectToAction(nameof(MySubmissions));
    }

    private sealed record BulkCsvRow(
        int RowNumber,
        Guid? Id,
        string Title,
        string Description,
        string Category,
        string Date,
        string StartTime,
        int DurationMinutes,
        string? LocationNote,
        string? Host,
        bool IsRecurring,
        string? RecurrenceDays,
        int PriorityRank);

    private static List<BulkCsvRow> ParseCsvRows(string csvText)
    {
        var rows = new List<BulkCsvRow>();
        var lines = csvText.Split('\n', StringSplitOptions.None);
        var dataLineNumber = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var fields = SplitCsvLine(line);

            // Header row — skip but validate column count
            if (dataLineNumber == 0)
            {
                dataLineNumber++;
                continue;
            }

            if (fields.Count < 14)
                throw new FormatException($"Row {dataLineNumber + 1}: expected 14 columns, got {fields.Count}.");

            Guid? id = string.IsNullOrWhiteSpace(fields[0]) ? null : Guid.TryParse(fields[0], out var g) ? g : throw new FormatException($"Row {dataLineNumber + 1}: Id is not a valid Guid.");
            // fields[1] = Barrio (ignored), fields[2] = Status (ignored)
            var title = fields[3];
            var description = fields[4];
            var category = fields[5];
            var date = fields[6];
            var startTime = fields[7];
            var durationStr = fields[8];
            var locationNote = fields[9];
            var host = fields[10];
            var isRecurringStr = fields[11];
            var recurrenceDays = fields[12];
            var priorityStr = fields[13];

            if (!int.TryParse(durationStr, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                throw new FormatException($"Row {dataLineNumber + 1}: DurationMinutes is not an integer.");
            if (!int.TryParse(priorityStr, System.Globalization.CultureInfo.InvariantCulture, out var priority))
                throw new FormatException($"Row {dataLineNumber + 1}: PriorityRank is not an integer.");
            var isRecurring = string.Equals(isRecurringStr, "true", StringComparison.OrdinalIgnoreCase);

            rows.Add(new BulkCsvRow(dataLineNumber + 1, id, title, description, category, date, startTime, duration,
                string.IsNullOrWhiteSpace(locationNote) ? null : locationNote,
                string.IsNullOrWhiteSpace(host) ? null : host,
                isRecurring, string.IsNullOrWhiteSpace(recurrenceDays) ? null : recurrenceDays, priority));
            dataLineNumber++;
        }

        return rows;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(string.Empty); break; }
            if (line[i] == '"')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        i++;
                        if (i < line.Length && line[i] == '"') { sb.Append('"'); i++; }
                        else break;
                    }
                    else { sb.Append(line[i]); i++; }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                var end = line.IndexOf(',', i);
                if (end < 0) { fields.Add(line[i..]); break; }
                fields.Add(line[i..end]);
                i = end + 1;
            }
        }
        return fields;
    }

    private static List<BulkRowError> ValidateBulkRows(
        List<BulkCsvRow> rows,
        IReadOnlyList<Domain.Entities.EventCategory> categories,
        IReadOnlyList<Domain.Entities.Event> existingEvents,
        NodaTime.LocalDate gateOpeningDate)
    {
        var errors = new List<BulkRowError>();
        foreach (var row in rows)
        {
            var rowErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(row.Title)) rowErrors.Add("Title is required.");
            else if (row.Title.Length > 80) rowErrors.Add("Title must be 80 characters or fewer.");

            if (string.IsNullOrWhiteSpace(row.Description)) rowErrors.Add("Description is required.");
            else if (row.Description.Length > 450) rowErrors.Add("Description must be 450 characters or fewer.");

            if (row.LocationNote?.Length > 120) rowErrors.Add("LocationNote must be 120 characters or fewer.");
            if (row.Host?.Length > 40) rowErrors.Add("Host must be 40 characters or fewer.");

            if (string.IsNullOrWhiteSpace(row.Category)) rowErrors.Add("Category is required.");
            else if (!categories.Any(c => string.Equals(c.Name, row.Category, StringComparison.OrdinalIgnoreCase)))
                rowErrors.Add($"Category '{row.Category}' is not a valid active category.");

            if (string.IsNullOrWhiteSpace(row.Date)) rowErrors.Add("Date is required.");
            else if (!DateTime.TryParseExact(row.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                rowErrors.Add("Date must be in yyyy-MM-dd format.");

            if (string.IsNullOrWhiteSpace(row.StartTime)) rowErrors.Add("StartTime is required.");
            else if (!TimeSpan.TryParseExact(row.StartTime, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out _))
                rowErrors.Add("StartTime must be in HH:mm format.");

            if (row.DurationMinutes < 15 || row.DurationMinutes > 480)
                rowErrors.Add("DurationMinutes must be between 15 and 480.");
            else if (row.DurationMinutes % 15 != 0)
                rowErrors.Add("DurationMinutes must be a multiple of 15.");

            if (row.PriorityRank < 1 || row.PriorityRank > 100)
                rowErrors.Add("PriorityRank must be between 1 and 100.");

            if (row.Id.HasValue)
            {
                var existing = existingEvents.FirstOrDefault(e => e.Id == row.Id.Value);
                if (existing == null)
                    rowErrors.Add($"Event {row.Id.Value} not found for this barrio.");
                else if (existing.Status == Domain.Enums.EventStatus.Withdrawn)
                    rowErrors.Add("Withdrawn events cannot be updated via bulk upload.");
            }

            if (rowErrors.Count > 0)
                errors.Add(new BulkRowError { RowNumber = row.RowNumber, Title = row.Title, Errors = rowErrors });
        }
        return errors;
    }

    private static string CsvField(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    private static string OffsetsToDisplayDays(string offsets, NodaTime.LocalDate gateOpeningDate)
    {
        var parts = offsets.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var names = new List<string>();
        foreach (var part in parts)
        {
            if (int.TryParse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                var date = gateOpeningDate.PlusDays(offset);
                names.Add(DayNames[((int)date.DayOfWeek - 1 + 7) % 7]);
            }
        }
        return string.Join(" ", names);
    }

    private static string? DisplayDaysToOffsets(string displayDays, NodaTime.LocalDate gateOpeningDate, int eventEndOffset)
    {
        var requestedDays = displayDays.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offsets = new List<int>();
        for (var offset = 0; offset <= eventEndOffset; offset++)
        {
            var date = gateOpeningDate.PlusDays(offset);
            var name = DayNames[((int)date.DayOfWeek - 1 + 7) % 7];
            if (requestedDays.Contains(name))
                offsets.Add(offset);
        }
        return offsets.Count > 0 ? string.Join(",", offsets) : null;
    }

    private static string ResolveCampDisplayName(CampLookup camp) =>
        camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name ?? camp.Slug;

    private async Task<CampEventFormViewModel> BuildBarrioFormAsync(string slug, CampLookup camp, BurnSettingsInfo burn)
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
                Label = date.ToString("ddd d MMM", null),
                Date = dt
            });
        }
    }

    private async Task<BurnSettingsInfo?> LoadBurnSettingsAsync(EventGuideSettings? guideSettings)
    {
        if (guideSettings == null) return null;
        return await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId);
    }

    internal static bool IsSubmitStateException(InvalidOperationException ex) =>
        ex.Message.StartsWith("Cannot submit event in ", StringComparison.Ordinal)
        && ex.Message.EndsWith(" state", StringComparison.Ordinal);

}
