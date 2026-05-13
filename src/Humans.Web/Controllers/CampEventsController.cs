using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.EventGuide;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Barrios/{slug}/Events")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class CampEventsController : HumansCampControllerBase
{
    private readonly IEventGuideService _guide;
    private readonly IClock _clock;
    private readonly IEmailService _emailService;
    private readonly ILogger<CampEventsController> _logger;

    public CampEventsController(
        UserManager<User> userManager,
        ICampService campService,
        IAuthorizationService authorizationService,
        IEventGuideService guide,
        IClock clock,
        IEmailService emailService,
        ILogger<CampEventsController> logger)
        : base(userManager, campService, authorizationService)
    {
        _guide = guide;
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
        var tz = GetTimeZone(guideSettings);
        var events = await _guide.GetCampSubmissionsAsync(camp.Id);

        var model = new CampEventsTabViewModel
        {
            CampId = camp.Id,
            CampName = ResolveCampName(camp),
            CampSlug = slug,
            IsSubmissionOpen = IsSubmissionOpen(guideSettings),
            SubmissionOpenAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionOpenAt, tz) : null,
            SubmissionCloseAt = guideSettings != null ? ToLocalDateTime(guideSettings.SubmissionCloseAt, tz) : null,
            TimeZoneId = guideSettings?.EventSettings?.TimeZoneId,
            SubmittedCount = events.Count,
            ApprovedCount = events.Count(e => e.Status == GuideEventStatus.Approved),
            PendingCount = events.Count(e => e.Status == GuideEventStatus.Pending),
            Events = events.Select(e => new CampEventRowViewModel
            {
                Id = e.Id,
                Title = e.Title,
                CategoryName = e.Category.Name,
                StartAt = ToLocalDateTime(e.StartAt, tz),
                DurationMinutes = e.DurationMinutes,
                Status = e.Status,
                PriorityRank = e.PriorityRank,
                CanEdit = e.Status is GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested or GuideEventStatus.Pending,
                CanWithdraw = e.Status == GuideEventStatus.Pending
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

        var model = await BuildFormAsync(slug, camp, guideSettings!);
        return View("CampEventForm", model);
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

        if (!ModelState.IsValid)
        {
            model.CampId = camp.Id;
            model.CampName = ResolveCampName(camp);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, guideSettings!);
            return View("CampEventForm", model);
        }

        var tz = GetTimeZone(guideSettings!);

        var guideEvent = new GuideEvent
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
            var viewUrl = Url.Action(nameof(Index), "CampEvents", new { slug }, Request.Scheme)!;
            await _emailService.SendEventSubmittedAsync(userEmail, user.UserName ?? userEmail, model.Title, viewUrl);
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

        if (guideEvent.Status is not (GuideEventStatus.Pending or GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested))
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

        var tz = GetTimeZone(guideSettings);
        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);

        var model = await BuildFormAsync(slug, camp, guideSettings);
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
        model.IsResubmit = guideEvent.Status is GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested;

        return View("CampEventForm", model);
    }

    [HttpPost("{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string slug, Guid eventId, CampEventFormViewModel model)
    {
        var (error, user, camp) = await ResolveCampManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await _guide.GetCampEventAsync(eventId, camp.Id);
        if (guideEvent == null) return NotFound();

        if (guideEvent.Status is not (GuideEventStatus.Pending or GuideEventStatus.Rejected or GuideEventStatus.ResubmitRequested))
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

        if (!ModelState.IsValid)
        {
            model.Id = eventId;
            model.CampId = camp.Id;
            model.CampName = ResolveCampName(camp);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, guideSettings);
            return View("CampEventForm", model);
        }

        var tz = GetTimeZone(guideSettings);

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

        if (guideEvent.Status != GuideEventStatus.Pending)
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

    private bool IsSubmissionOpen(GuideSettings? settings)
    {
        if (settings == null) return false;
        var now = _clock.GetCurrentInstant();
        return now >= settings.SubmissionOpenAt && now <= settings.SubmissionCloseAt;
    }

    private async Task<CampEventFormViewModel> BuildFormAsync(string slug, CampLookup camp, GuideSettings guideSettings)
    {
        var model = new CampEventFormViewModel
        {
            CampId = camp.Id,
            CampName = ResolveCampName(camp),
            CampSlug = slug,
            TimeZoneId = guideSettings.EventSettings.TimeZoneId
        };
        await PopulateDropdownsAsync(model, guideSettings);
        return model;
    }

    private async Task PopulateDropdownsAsync(CampEventFormViewModel model, GuideSettings guideSettings)
    {
        var categories = await _guide.GetActiveCategoriesAsync();
        model.Categories = categories.Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList();
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

    private static string ResolveCampName(CampLookup camp)
    {
        var currentSeason = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        return currentSeason?.Name ?? camp.Slug;
    }

    private static DateTimeZone? GetTimeZone(GuideSettings? guideSettings)
        => guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();

    private static Instant ToInstant(DateTime dateTime, DateTimeZone? tz)
    {
        if (tz == null)
            return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        return LocalDateTime.FromDateTime(dateTime).InZoneLeniently(tz).ToInstant();
    }
}
