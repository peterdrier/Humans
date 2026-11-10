using Humans.Events.Services.Dtos;
using Humans.Events.Services;
using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Events.Domain;
using Humans.Base.Authorization;
using Humans.Events.Filters;
using Humans.Events.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using static Humans.Events.Helpers.EventsLookupHelpers;
using static Humans.Events.Helpers.EventsTimeHelpers;
using Humans.Events.Contracts;
using Humans.Users.Contracts;

namespace Humans.Events.Controllers;

[Authorize]
[Route("Events")]
[ServiceFilter(typeof(EventsFeatureFilter))]
internal sealed class EventsController(
    IEventManagement guide,
    IUserService users,
    ICampService camps,
    IAuthorizationService authorizationService,
    IClock clock,
    ILogger<EventsController> logger,
    IStringLocalizer<EventsResource> localizer) : HumansCampControllerBase(users, camps, authorizationService)
{
    private readonly EventGuidePageBuilder guidePageBuilder = new(guide, camps);

    [HttpGet("MySubmissions")]
    public async Task<IActionResult> MySubmissions()
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user == null) return Challenge();

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct);
        var isSubmissionOpen = IsSubmissionOpen(guideSettings);

        var tz = GetTimeZone(eventSettings);

        var individualEvents = await guide.GetUserSubmissionsAsync(user.Id, ct);

        // Camps the user leads or runs workshops for.
        var managedCamps = (await LoadCampsByIdAsync(CampService, guideSettings?.EventSettingsId, ct)).Values
            .Where(camp => camp.IsEventManager(user.Id))
            .ToList();

        var barrioBlocks = new List<BarrioSubmissionsBlock>();
        foreach (var camp in managedCamps)
        {
            var summary = await guide.GetCampSubmissionsSummaryAsync(camp.Id, ct);
            var campName = ResolveCampDisplayName(camp, guideSettings?.EventSettingsId);
            barrioBlocks.Add(new BarrioSubmissionsBlock
            {
                CampId = camp.Id,
                CampName = campName,
                CampSlug = camp.Slug,
                SubmittedCount = summary.SubmittedCount,
                ApprovedCount = summary.ApprovedCount,
                PendingCount = summary.PendingCount,
                Events = summary.Events.OrderByDescending(e => e.SubmittedAt).Select(e => new CampEventRowViewModel
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
        var ct = HttpContext.RequestAborted;
        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError(localizer["Events_SubmissionWindowClosed"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var model = await BuildFormAsync(eventSettings, ct);
        return View("IndividualEventForm", model);
    }

    [HttpPost("Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(IndividualEventFormViewModel model)
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user == null) return Challenge();

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError(localizer["Events_SubmissionWindowClosed"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");

        ValidateRecurrenceDays(model.IsRecurring, model.RecurrenceDays, eventSettings);
        ValidateSchedule(model.StartDate,
            model.IsAllDay ? TimeOnly.MinValue : model.StartTime,
            model.IsAllDay ? Event.AllDayDurationMinutes : model.DurationMinutes, eventSettings);

        if (!ModelState.IsValid)
        {
            await PopulateDropdownsAsync(model, eventSettings, ct);
            return View("IndividualEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);
        var (startTime, durationMinutes) = Event.ResolveAllDaySchedule(
            model.IsAllDay, model.StartTime.ToTimeSpan(), model.DurationMinutes);

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
            StartAt = ToInstant(model.StartDate.ToDateTime(TimeOnly.FromTimeSpan(startTime)), tz),
            DurationMinutes = durationMinutes,
            IsRecurring = model.IsRecurring,
            RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null,
        };

        // The service validates the window, recurrence and selections before the Submit transition.
        var viewUrl = Url.Action(nameof(MySubmissions), "Events", null, Request.Scheme)!;
        var result = await guide.SubmitEventAsync(guideEvent, viewUrl);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Event submission refused for category {CategoryId}: {ErrorKey}", model.CategoryId, errorKey);
            if (string.Equals(errorKey, "Events_SubmissionWindowClosed", StringComparison.Ordinal))
            {
                SetError(localizer[errorKey].Value);
                return RedirectToAction(nameof(MySubmissions));
            }
            ModelState.AddModelError(result.Field ?? string.Empty, localizer[errorKey].Value);
            await PopulateDropdownsAsync(model, eventSettings);
            return View("IndividualEventForm", model);
        }

        logger.LogInformation("User {UserId} submitted individual event '{Title}'", user.Id, model.Title);

        SetSuccess(FormatEventFeedback("Events_SubmittedForReview", model.Title));
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Submit/{eventId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid eventId)
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user == null) return Challenge();

        var editable = await RequireEditableIndividualEventAsync(eventId, user.Id, ct);
        if (editable.Error is not null) return editable.Error;
        var guideEvent = editable.Event!;

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        if (guideSettings == null)
        {
            SetError(localizer["Events_GuideSettingsUnavailable"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var tz = GetTimeZone(eventSettings);
        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);

        var model = await BuildFormAsync(eventSettings, ct);
        PopulateProposalForm(model, guideEvent, localStart);
        model.VenueId = guideEvent.GuideSharedVenueId ?? Guid.Empty;
        model.IsAllDay = guideEvent.IsAllDay;
        model.DurationMinutes = guideEvent.DurationMinutes;

        return View("IndividualEventForm", model);
    }

    [HttpPost("Submit/{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid eventId, IndividualEventFormViewModel model)
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user == null) return Challenge();

        var editable = await RequireEditableIndividualEventAsync(eventId, user.Id, ct);
        if (editable.Error is not null) return editable.Error;

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        if (guideSettings == null)
        {
            SetError(localizer["Events_GuideSettingsUnavailable"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");

        ValidateRecurrenceDays(model.IsRecurring, model.RecurrenceDays, eventSettings);
        ValidateSchedule(model.StartDate,
            model.IsAllDay ? TimeOnly.MinValue : model.StartTime,
            model.IsAllDay ? Event.AllDayDurationMinutes : model.DurationMinutes, eventSettings);

        if (!ModelState.IsValid)
        {
            model.Id = eventId;
            await PopulateDropdownsAsync(model, eventSettings, ct);
            return View("IndividualEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);
        var (startTime, durationMinutes) = Event.ResolveAllDaySchedule(
            model.IsAllDay, model.StartTime.ToTimeSpan(), model.DurationMinutes);

        var result = await guide.UpdateAndResubmitAsync(eventId, new EventEdit(
            model.CategoryId, model.VenueId, model.Title, model.Description,
            ToInstant(model.StartDate.ToDateTime(TimeOnly.FromTimeSpan(startTime)), tz), durationMinutes,
            model.LocationNote, model.Host, model.IsRecurring, model.RecurrenceDays, PriorityRank: null));
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Event resubmission refused for event {EventId}, category {CategoryId}: {ErrorKey}", eventId, model.CategoryId, errorKey);
            ModelState.AddModelError(result.Field ?? string.Empty, localizer[errorKey].Value);
            model.Id = eventId;
            await PopulateDropdownsAsync(model, eventSettings);
            return View("IndividualEventForm", model);
        }

        logger.LogInformation("User {UserId} updated event '{Title}' ({EventId})", user.Id, model.Title, eventId);

        SetSuccess(FormatEventFeedback("Events_ResubmittedForReview", model.Title));
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpPost("Submit/{eventId:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid eventId)
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user == null) return Challenge();

        var guideEvent = await guide.GetUserEventAsync(eventId, user.Id, ct);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanWithdraw)
        {
            SetError(localizer["Events_NotWithdrawable"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        await guide.WithdrawEventAsync(guideEvent.Id);

        logger.LogInformation("User {UserId} withdrew event '{Title}' ({EventId})", user.Id, guideEvent.Title, eventId);
        SetSuccess(FormatEventFeedback("Events_Withdrawn", guideEvent.Title));
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Schedule")]
    public async Task<IActionResult> Schedule()
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user == null) return Challenge();

        var model = await guidePageBuilder.BuildScheduleAsync(user.Id, ct);

        return View(model);
    }

    [HttpGet("Browse")]
    public async Task<IActionResult> Browse(
        [FromQuery(Name = "days")] int[]? days, Guid? categoryId, Guid? venueId, string? q, bool favouritesOnly = false)
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user == null) return Challenge();

        var model = await guidePageBuilder.BuildBrowseAsync(user.Id, days, categoryId, venueId, q, favouritesOnly, ct);

        return View(model);
    }

    private async Task<(IActionResult? Error, EventInfo? Event)> RequireEditableIndividualEventAsync(
        Guid eventId, Guid userId, CancellationToken ct = default)
    {
        var guideEvent = await guide.GetEventForModerationAsync(eventId, ct);
        if (guideEvent is null || guideEvent.CampId != null) return (NotFound(), null);
        if (guideEvent.SubmitterUserId != userId && !EventsRoles.IsEventsAdmin(User))
            return (Forbid(), null);
        if (!guideEvent.CanEdit)
        {
            SetError(localizer["Events_NotEditable"].Value);
            return (RedirectToAction(nameof(MySubmissions)), null);
        }
        return (null, guideEvent);
    }

    private bool IsSubmissionOpen(EventGuideSettingsView? settings) =>
        settings?.IsSubmissionOpenAt(clock.GetCurrentInstant()) ?? false;

    private void ValidateSchedule(DateOnly date, TimeOnly time, int durationMinutes, EventSettingsInfo settings)
    {
        if (!ModelState.IsValid || CanDisplaySchedule(
            LocalDateTime.FromDateTime(date.ToDateTime(time)), durationMinutes, GetTimeZone(settings))) return;
        logger.LogWarning("Event form rejected: schedule is outside the supported calendar range");
        ModelState.AddModelError(nameof(IndividualEventFormViewModel.StartDate),
            localizer["Events_Upload_DateRange"].Value);
    }

    private void ValidateRecurrenceDays(bool isRecurring, string? days, EventSettingsInfo settings)
    {
        if (!isRecurring || EventRecurrenceDays.HasOnlyEventOffsets(days, settings.EventEndOffset)) return;
        logger.LogWarning("Event form rejected: recurrence days are outside the configured calendar or invalid");
        ModelState.AddModelError(nameof(IndividualEventFormViewModel.RecurrenceDays),
            localizer["Events_RecurrenceDaysInvalid"].Value);
    }

    private static void PopulateProposalForm(EventProposalFormViewModel model, EventInfo guideEvent, DateTime localStart)
    {
        model.Id = guideEvent.Id;
        model.Title = guideEvent.Title;
        model.Description = guideEvent.Description;
        model.CategoryId = guideEvent.CategoryId;
        model.StartDate = DateOnly.FromDateTime(localStart);
        model.StartTime = TimeOnly.FromDateTime(localStart);
        model.LocationNote = guideEvent.LocationNote;
        model.Host = guideEvent.Host;
        model.IsRecurring = guideEvent.IsRecurring;
        model.RecurrenceDays = guideEvent.RecurrenceDays;
        model.IsResubmit = guideEvent.IsResubmission;
    }

    private async Task<IndividualEventFormViewModel> BuildFormAsync(EventSettingsInfo burn, CancellationToken ct = default)
    {
        var model = new IndividualEventFormViewModel
        {
            TimeZoneId = burn.TimeZoneId
        };
        await PopulateDropdownsAsync(model, burn, ct);
        return model;
    }

    private async Task PopulateDropdownsAsync(EventProposalFormViewModel model, EventSettingsInfo burn, CancellationToken ct = default)
    {
        var categories = await guide.GetActiveCategoriesAsync(ct);
        var venues = model is IndividualEventFormViewModel
            ? await guide.GetActiveVenuesAsync(ct)
            : [];

        model.Categories = categories.OrderBy(c => c.DisplayOrder).Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList();
        if (model is IndividualEventFormViewModel individual)
            individual.Venues = venues.OrderBy(v => v.DisplayOrder).Select(v => new VenueOptionViewModel { Id = v.Id, Name = v.Name }).ToList();
        model.TimeZoneId = burn.TimeZoneId;

        model.EventDays = BuildEventDayOptions(burn);
    }

    [HttpGet("Barrio/{slug}/Submit")]
    public async Task<IActionResult> BarrioSubmit(string slug)
    {
        var ct = HttpContext.RequestAborted;
        var (error, _, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError(localizer["Events_SubmissionWindowClosed"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var model = await BuildBarrioFormAsync(slug, camp, eventSettings, ct);
        return View("BarrioEventForm", model);
    }

    [HttpPost("Barrio/{slug}/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BarrioCreate(string slug, CampEventFormViewModel model)
    {
        var ct = HttpContext.RequestAborted;
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError(localizer["Events_SubmissionWindowClosed"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");

        ValidateRecurrenceDays(model.IsRecurring, model.RecurrenceDays, eventSettings);
        ValidateSchedule(model.StartDate, model.StartTime, model.DurationMinutes, eventSettings);

        if (!ModelState.IsValid)
        {
            model.CampId = camp.Id;
            model.CampName = ResolveCampDisplayName(camp, eventSettings.Id);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, eventSettings, ct);
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
            StartAt = ToInstant(model.StartDate.ToDateTime(model.StartTime), tz),
            DurationMinutes = model.DurationMinutes,
            IsRecurring = model.IsRecurring,
            RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null,
            PriorityRank = model.PriorityRank
        };

        // The service validates the window, recurrence and selections before the Submit transition.
        var viewUrl = Url.Action(nameof(MySubmissions), "Events", null, Request.Scheme)!;
        var result = await guide.SubmitEventAsync(guideEvent, viewUrl);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Event submission refused for category {CategoryId}: {ErrorKey}", model.CategoryId, errorKey);
            if (string.Equals(errorKey, "Events_SubmissionWindowClosed", StringComparison.Ordinal))
            {
                SetError(localizer[errorKey].Value);
                return RedirectToAction(nameof(MySubmissions));
            }
            ModelState.AddModelError(result.Field ?? string.Empty, localizer[errorKey].Value);
            model.CampId = camp.Id;
            model.CampName = ResolveCampDisplayName(camp, eventSettings.Id);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, eventSettings);
            return View("BarrioEventForm", model);
        }
        logger.LogInformation("User {UserId} submitted barrio event '{Title}' for camp {CampId}", user.Id, model.Title, camp.Id);

        SetSuccess(FormatEventFeedback("Events_SubmittedForReview", model.Title));
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Barrio/{slug}/{eventId:guid}/Edit")]
    public async Task<IActionResult> BarrioEdit(string slug, Guid eventId)
    {
        var ct = HttpContext.RequestAborted;
        var (error, _, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await guide.GetCampEventAsync(eventId, camp.Id, ct);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanEdit)
        {
            SetError(localizer["Events_NotEditable"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await guide.GetGuideSettingsAsync(ct)
            ?? throw new InvalidOperationException("Guide settings not configured.");
        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var tz = GetTimeZone(eventSettings);
        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);

        var model = await BuildBarrioFormAsync(slug, camp, eventSettings, ct);
        PopulateProposalForm(model, guideEvent, localStart);
        model.DurationMinutes = guideEvent.DurationMinutes;
        model.PriorityRank = guideEvent.PriorityRank;

        return View("BarrioEventForm", model);
    }

    [HttpPost("Barrio/{slug}/{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BarrioUpdate(string slug, Guid eventId, CampEventFormViewModel model)
    {
        var ct = HttpContext.RequestAborted;
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await guide.GetCampEventAsync(eventId, camp.Id, ct);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanEdit)
        {
            SetError(localizer["Events_NotEditable"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var guideSettings = await guide.GetGuideSettingsAsync(ct)
            ?? throw new InvalidOperationException("Guide settings not configured.");
        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");

        ValidateRecurrenceDays(model.IsRecurring, model.RecurrenceDays, eventSettings);
        ValidateSchedule(model.StartDate, model.StartTime, model.DurationMinutes, eventSettings);

        if (!ModelState.IsValid)
        {
            model.Id = eventId;
            model.CampId = camp.Id;
            model.CampName = ResolveCampDisplayName(camp, eventSettings.Id);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, eventSettings, ct);
            return View("BarrioEventForm", model);
        }

        var tz = GetTimeZone(eventSettings);
        var result = await guide.UpdateAndResubmitAsync(eventId, new EventEdit(
            model.CategoryId, VenueId: null, model.Title, model.Description,
            ToInstant(model.StartDate.ToDateTime(model.StartTime), tz), model.DurationMinutes,
            model.LocationNote, model.Host, model.IsRecurring, model.RecurrenceDays, model.PriorityRank));
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Event resubmission refused for event {EventId}, category {CategoryId}: {ErrorKey}", eventId, model.CategoryId, errorKey);
            ModelState.AddModelError(result.Field ?? string.Empty, localizer[errorKey].Value);
            model.Id = eventId;
            model.CampId = camp.Id;
            model.CampName = ResolveCampDisplayName(camp, eventSettings.Id);
            model.CampSlug = slug;
            await PopulateDropdownsAsync(model, eventSettings);
            return View("BarrioEventForm", model);
        }

        logger.LogInformation("User {UserId} updated barrio event '{Title}' ({EventId})", user.Id, model.Title, eventId);

        SetSuccess(FormatEventFeedback("Events_ResubmittedForReview", model.Title));
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpPost("Barrio/{slug}/{eventId:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BarrioWithdraw(string slug, Guid eventId)
    {
        var ct = HttpContext.RequestAborted;
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideEvent = await guide.GetCampEventAsync(eventId, camp.Id, ct);
        if (guideEvent == null) return NotFound();

        if (!guideEvent.CanWithdraw)
        {
            SetError(localizer["Events_NotWithdrawable"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        await guide.WithdrawEventAsync(guideEvent.Id);
        logger.LogInformation("User {UserId} withdrew barrio event '{Title}' ({EventId})", user.Id, guideEvent.Title, eventId);

        SetSuccess(FormatEventFeedback("Events_Withdrawn", guideEvent.Title));
        return RedirectToAction(nameof(MySubmissions));
    }

    [HttpGet("Barrio/{slug}/BulkUpload/Template")]
    public async Task<IActionResult> BulkUploadTemplate(string slug)
    {
        var ct = HttpContext.RequestAborted;
        var (error, _, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        var bytes = await guide.BuildBulkUploadTemplateAsync(
            camp.Id, ResolveCampDisplayName(camp, guideSettings?.EventSettingsId), ct);
        return File(bytes, "text/csv", $"{slug}-events.csv");
    }

    [HttpPost("Barrio/{slug}/BulkUpload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(1 * 1024 * 1024)]
    public async Task<IActionResult> BulkUploadImport(string slug, IFormFile? file)
    {
        var ct = HttpContext.RequestAborted;
        var (error, user, camp) = await ResolveCampEventManagementAsync(slug);
        if (error != null) return error;

        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        if (!IsSubmissionOpen(guideSettings))
        {
            SetError(localizer["Events_SubmissionWindowClosed"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        if (file == null || file.Length == 0)
        {
            SetError(localizer["Events_UploadSelectCsv"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct)
            ?? throw new InvalidOperationException("Event settings not configured.");
        var tz = GetTimeZone(eventSettings)
            ?? throw new InvalidOperationException("Event timezone not configured.");

        BulkCsvParseResult parsed;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8);
            parsed = BulkEventCsvParser.Parse(await reader.ReadToEndAsync(HttpContext.RequestAborted));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (CsvHelper.CsvFormatException ex)
        {
            logger.LogWarning("Bulk CSV parse failed for camp slug {Slug}: {Message}", slug, ex.Message);
            SetError(localizer["Events_Upload_ParseFailed"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bulk CSV parse failed for camp slug {Slug}: {Message}", slug, ex.Message);
            SetError(localizer["Events_Upload_ParseFailed"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        if (parsed.Errors.Count > 0)
        {
            logger.LogWarning("Bulk CSV validation failed for camp slug {Slug}: {ErrorKeys}",
                slug, string.Join(", ", parsed.Errors.Select(error => error.Key)));
            var detail = string.Join(" ", parsed.Errors.Select(error => localizer[error.Key, error.Args].Value));
            SetError(localizer["Events_Upload_ParseErrorDetail", detail].Value);
            return RedirectToAction(nameof(MySubmissions));
        }
        var rows = parsed.Rows;

        if (rows.Count == 0)
        {
            SetError(localizer["Events_UploadNoRows"].Value);
            return RedirectToAction(nameof(MySubmissions));
        }

        var result = await guide.BulkImportAsync(
            camp.Id, user.Id, rows,
            eventSettings.GateOpeningDate, eventSettings.EventEndOffset, tz);

        return BulkUploadResult(slug, user.Id, camp.Id, result);
    }

    private IActionResult BulkUploadResult(string slug, Guid userId, Guid campId, BulkImportResult result)
    {
        if (result.RefusalKey is { } refusalKey)
        {
            SetError(localizer[refusalKey].Value);
            return RedirectToAction(nameof(MySubmissions));
        }
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
            userId, campId, result.CreatedCount, result.UpdatedCount);
        SetSuccess(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            localizer["Events_UploadComplete"].Value,
            result.CreatedCount,
            result.UpdatedCount));
        return RedirectToAction(nameof(MySubmissions));
    }


    // camp is non-null at every call site; Slug is always set, so a name always resolves.
    private static string ResolveCampDisplayName(CampInfo camp, Guid? eventSettingsId) =>
        ResolveCampName(camp, eventSettingsId)!;

    private string FormatEventFeedback(string resourceKey, string title) =>
        string.Format(localizer[resourceKey].Value, title);

    private async Task<CampEventFormViewModel> BuildBarrioFormAsync(string slug, CampInfo camp, EventSettingsInfo burn, CancellationToken ct = default)
    {
        var model = new CampEventFormViewModel
        {
            CampId = camp.Id,
            CampName = ResolveCampDisplayName(camp, burn.Id),
            CampSlug = slug,
            TimeZoneId = burn.TimeZoneId
        };
        await PopulateDropdownsAsync(model, burn, ct);
        return model;
    }
}
