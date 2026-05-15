using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using static Humans.Web.Helpers.EventsTimeHelpers;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Barrios/{slug}/Events")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class BarrioEventsController : HumansCampControllerBase
{
    private readonly IEventService _guide;
    private readonly IUserService _users;
    private readonly IClock _clock;
    private readonly IEmailService _emailService;
    private readonly ILogger<BarrioEventsController> _logger;

    public BarrioEventsController(
        UserManager<User> userManager,
        ICampService campService,
        IAuthorizationService authorizationService,
        IEventService guide,
        IUserService users,
        IClock clock,
        IEmailService emailService,
        ILogger<BarrioEventsController> logger)
        : base(userManager, campService, authorizationService)
    {
        _guide = guide;
        _users = users;
        _clock = clock;
        _emailService = emailService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug)
    {
        var (error, _, camp) = await ResolveCampManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await _guide.GetGuideSettingsAsync();
        var eventSettings = await LoadEventSettingsAsync(guideSettings);
        var tz = GetTimeZone(eventSettings);
        var events = await _guide.GetCampSubmissionsAsync(camp.Id);

        var model = new CampEventsTabViewModel
        {
            CampId = camp.Id,
            CampName = ResolveCampName(camp),
            CampSlug = slug,
            IsSubmissionOpen = IsSubmissionOpen(guideSettings),
            SubmissionOpenAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionOpenAt, tz) : null,
            SubmissionCloseAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionCloseAt, tz) : null,
            TimeZoneId = eventSettings?.TimeZoneId,
            SubmittedCount = events.Count,
            ApprovedCount = events.Count(e => e.Status == EventStatus.Approved),
            PendingCount = events.Count(e => e.Status == EventStatus.Pending),
            Events = events.Select(e => new CampEventRowViewModel
            {
                Id = e.Id,
                Title = e.Title,
                CategoryName = e.Category.Name,
                StartAt = ToLocalDateTime(e.StartAt, tz),
                DurationMinutes = e.DurationMinutes,
                Status = e.Status,
                PriorityRank = e.PriorityRank,
                CanEdit = e.Status is EventStatus.Rejected or EventStatus.ResubmitRequested or EventStatus.Pending,
                CanWithdraw = e.Status == EventStatus.Pending
            }).ToList()
        };

        return View(model);
    }

    [HttpGet("New")]
    public async Task<IActionResult> New(string slug)
    {
        var (error, _, camp) = await ResolveCampManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await _guide.GetGuideSettingsAsync();
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var eventSettings = await LoadEventSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var model = await BuildFormAsync(slug, camp, eventSettings);
        return View("BarrioEventForm", model);
    }

    [HttpPost("New")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string slug, CampEventFormViewModel model)
    {
        var (error, user, camp) = await ResolveCampManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await _guide.GetGuideSettingsAsync();
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError("The submission window is not currently open.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var eventSettings = await LoadEventSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");

        if (!ModelState.IsValid)
        {
            model.CampId = camp.Id;
            model.CampName = ResolveCampName(camp);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, eventSettings);
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
            StartAt = ToInstant(model.StartDate.Add(model.StartTime), tz),
            DurationMinutes = model.DurationMinutes,
            IsRecurring = model.IsRecurring,
            RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null,
            PriorityRank = model.PriorityRank
        };
        guideEvent.Submit(_clock);

        await _guide.SubmitEventAsync(guideEvent);

        _logger.LogInformation("User {UserId} submitted event '{Title}' for camp {CampId}",
            user.Id, model.Title, camp.Id);

        var userEmail = user.Email;
        if (userEmail != null)
        {
            var userInfo = await _users.GetUserInfoAsync(user.Id);
            var viewUrl = Url.Action(nameof(Index), "BarrioEvents", new { slug }, Request.Scheme)!;
            await _emailService.SendEventLifecycleNotificationAsync(
                new EventLifecycleNotification(
                    NewStatus: EventStatus.Pending,
                    UserName: userInfo?.DisplayName ?? userEmail,
                    EventTitle: model.Title,
                    ActionUrl: viewUrl),
                userEmail);
        }

        SetSuccess($"Event \"{model.Title}\" submitted for review.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpGet("{eventId:guid}/Edit")]
    public async Task<IActionResult> Edit(string slug, Guid eventId)
    {
        var (error, _, camp) = await ResolveCampManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await _guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested))
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var guideSettings = await _guide.GetGuideSettingsAsync();
        if (guideSettings == null)
        {
            SetError("Guide settings not configured.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var eventSettings = await LoadEventSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var tz = GetTimeZone(eventSettings);
        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);

        var model = await BuildFormAsync(slug, camp, eventSettings);
        model.Id = guideEvent.Id;
        model.Title = guideEvent.Title;
        model.Description = guideEvent.Description;
        model.CategoryId = guideEvent.CategoryId;
        model.StartDate = localStart.Date;
        model.StartTime = localStart.TimeOfDay;
        model.DurationMinutes = guideEvent.DurationMinutes;
        model.LocationNote = guideEvent.LocationNote;
        model.IsRecurring = guideEvent.IsRecurring;
        model.RecurrenceDays = guideEvent.RecurrenceDays;
        model.PriorityRank = guideEvent.PriorityRank;
        model.IsResubmit = guideEvent.Status is EventStatus.Rejected or EventStatus.ResubmitRequested;

        return View("BarrioEventForm", model);
    }

    [HttpPost("{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string slug, Guid eventId, CampEventFormViewModel model)
    {
        var (error, user, camp) = await ResolveCampManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await _guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested))
        {
            SetError("This event cannot be edited in its current state.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var guideSettings = await _guide.GetGuideSettingsAsync();
        if (guideSettings == null)
        {
            SetError("Guide settings not configured.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var eventSettings = await LoadEventSettingsAsync(guideSettings)
            ?? throw new InvalidOperationException("Event settings not configured.");

        if (!ModelState.IsValid)
        {
            model.Id = eventId;
            model.CampId = camp.Id;
            model.CampName = ResolveCampName(camp);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, eventSettings);
            return View("BarrioEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);

        guideEvent.Title = model.Title;
        guideEvent.Description = model.Description;
        guideEvent.CategoryId = model.CategoryId;
        guideEvent.StartAt = ToInstant(model.StartDate.Add(model.StartTime), tz);
        guideEvent.DurationMinutes = model.DurationMinutes;
        guideEvent.LocationNote = model.LocationNote;
        guideEvent.IsRecurring = model.IsRecurring;
        guideEvent.RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null;
        guideEvent.PriorityRank = model.PriorityRank;

        await _guide.UpdateAndResubmitAsync(guideEvent);

        _logger.LogInformation("User {UserId} updated event '{Title}' ({EventId})",
            user.Id, model.Title, eventId);

        SetSuccess($"Event \"{model.Title}\" resubmitted for review.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("{eventId:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(string slug, Guid eventId)
    {
        var (error, user, camp) = await ResolveCampManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await _guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status != EventStatus.Pending)
        {
            SetError("This event cannot be withdrawn in its current state.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        await _guide.WithdrawEventAsync(guideEvent);

        _logger.LogInformation("User {UserId} withdrew event '{Title}' ({EventId})",
            user.Id, guideEvent.Title, eventId);

        SetSuccess($"Event \"{guideEvent.Title}\" withdrawn.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private bool IsSubmissionOpen(EventGuideSettings? settings) =>
        settings?.IsSubmissionOpenAt(_clock.GetCurrentInstant()) ?? false;

    private async Task<CampEventFormViewModel> BuildFormAsync(string slug, CampLookup camp, EventSettings eventSettings)
    {
        var model = new CampEventFormViewModel
        {
            CampId = camp.Id,
            CampName = ResolveCampName(camp),
            CampSlug = slug,
            TimeZoneId = eventSettings.TimeZoneId
        };
        await PopulateDropdownsAsync(model, eventSettings);
        return model;
    }

    private async Task PopulateDropdownsAsync(CampEventFormViewModel model, EventSettings eventSettings)
    {
        var categories = await _guide.GetActiveCategoriesAsync();
        model.Categories = categories.Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList();
        model.TimeZoneId = eventSettings.TimeZoneId;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId);

        model.EventDays = [];
        for (var offset = 0; offset <= eventSettings.EventEndOffset; offset++)
        {
            var date = eventSettings.GateOpeningDate.PlusDays(offset);
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

    private async Task<EventSettings?> LoadEventSettingsAsync(EventGuideSettings? guideSettings)
    {
        if (guideSettings == null) return null;
        return await _guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId);
    }

    private static string ResolveCampName(CampLookup camp)
    {
        var currentSeason = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        return currentSeason?.Name ?? camp.Slug;
    }

}
