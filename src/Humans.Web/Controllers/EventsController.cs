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
    ICampService camps,
    IUserService users,
    IUserService userService,
    IClock clock,
    IEmailService emailService,
    ILogger<EventsController> logger) : HumansControllerBase(userService)
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

        var events = await guide.GetUserSubmissionsAsync(user.Id);

        var model = new MySubmissionsViewModel
        {
            IsSubmissionOpen = isSubmissionOpen,
            SubmissionOpenAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionOpenAt, tz) : null,
            SubmissionCloseAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionCloseAt, tz) : null,
            TimeZoneId = eventSettings?.TimeZoneId,
            SubmittedCount = events.Count,
            ApprovedCount = events.Count(e => e.Status == EventStatus.Approved),
            PendingCount = events.Count(e => e.Status == EventStatus.Pending),
            Events = events.Select(e => new IndividualEventRowViewModel
            {
                Id = e.Id,
                Title = e.Title,
                VenueName = e.EventVenue?.Name ?? "—",
                CategoryName = e.Category.Name,
                StartAt = ToLocalDateTime(e.StartAt, tz),
                DurationMinutes = e.DurationMinutes,
                Status = e.Status,
                CanEdit = e.Status is EventStatus.Draft or EventStatus.Rejected or EventStatus.ResubmitRequested,
                CanWithdraw = e.Status is EventStatus.Draft or EventStatus.Pending
            }).ToList()
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
            var userInfo = await users.GetUserInfoAsync(user.Id);
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

        if (guideEvent.Status is not (EventStatus.Draft or EventStatus.Rejected or EventStatus.ResubmitRequested))
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

        if (guideEvent.Status is not (EventStatus.Draft or EventStatus.Rejected or EventStatus.ResubmitRequested))
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
        guideEvent.IsRecurring = model.IsRecurring;
        guideEvent.RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null;

        await guide.UpdateAndResubmitAsync(guideEvent);

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

        if (guideEvent.Status is not (EventStatus.Draft or EventStatus.Pending))
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
        var campsById = await LoadCampsByIdAsync(camps, gateOpeningDate?.Year);

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

        var campsById = await LoadCampsByIdAsync(camps, gateOpeningDate?.Year);
        var individualSubmitterIds = events.Where(e => e.CampId == null).Select(e => e.SubmitterUserId).Distinct();
        var submitterInfoById = await LoadSubmittersAsync(users, individualSubmitterIds);

        var items = new List<BrowseEventItem>();
        foreach (var e in events)
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var seasonName = camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name;
            var campName = seasonName ?? camp?.Slug;
            var submitterName = e.CampId == null
                ? submitterInfoById.GetValueOrDefault(e.SubmitterUserId)?.BurnerName
                : null;

            foreach (var startInstant in e.GetOccurrenceInstants())
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
                    SubmitterName = submitterName
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

    private async Task<BurnSettingsInfo?> LoadBurnSettingsAsync(EventGuideSettings? guideSettings)
    {
        if (guideSettings == null) return null;
        return await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId);
    }


}
