using Humans.Application;
using Humans.Application.Events;
using Humans.Application.Interfaces.Camps;
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

[Authorize(Policy = PolicyNames.EventsAdminOrAdmin)]
[Route("Events/Moderate")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class EventsModerationController(
    IEventService guide,
    IUserServiceRead userService,
    ICampServiceRead camps,
    ILogger<EventsModerationController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] EventStatus? tab)
    {
        var activeTab = tab ?? EventStatus.Pending;

        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = guideSettings != null
            ? await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId)
            : null;
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;

        var counts = await guide.GetEventStatusCountsAsync();
        var unsortedEvents = await guide.GetEventsByStatusAsync(activeTab);
        var events = (activeTab == EventStatus.Pending
            ? unsortedEvents.OrderBy(e => e.SubmittedAt)
            : unsortedEvents.OrderByDescending(e => e.SubmittedAt)).ToList();

        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.GateOpeningDate.Year);
        var submitterInfoById = await LoadSubmittersAsync(UserService, events.Select(e => e.SubmitterUserId).Distinct());

        var model = new ModerationQueueViewModel
        {
            ActiveTab = activeTab,
            PendingCount = counts.GetValueOrDefault(EventStatus.Pending),
            ApprovedCount = counts.GetValueOrDefault(EventStatus.Approved),
            RejectedCount = counts.GetValueOrDefault(EventStatus.Rejected),
            ResubmitRequestedCount = counts.GetValueOrDefault(EventStatus.ResubmitRequested),
            WithdrawnCount = counts.GetValueOrDefault(EventStatus.Withdrawn),
            TimeZoneId = eventSettings?.TimeZoneId,
            Events = events.Select(e => BuildRow(e, tz, campsById, submitterInfoById)).ToList()
        };

        // Duplicate detection for camp events
        var campEvents = events.Where(e => e.CampId.HasValue).ToList();
        if (campEvents.Count > 0)
        {
            var allCampEvents = await guide.GetCampEventsForOverlapAsync();

            foreach (var row in model.Events)
            {
                var evt = campEvents.FirstOrDefault(e => e.Id == row.Id);
                if (evt?.CampId == null) continue;

                row.DuplicateCandidates = allCampEvents
                    .Where(other => other.Id != evt.Id
                                 && other.CampId == evt.CampId
                                 && EventConflictDetector.Overlaps(
                                     other.StartAt, other.DurationMinutes,
                                     evt.StartAt, evt.DurationMinutes))
                    .Select(other => new DuplicateCandidateViewModel
                    {
                        Id = other.Id,
                        Title = other.Title,
                        StartAt = ToLocalDateTime(other.StartAt, tz),
                        DurationMinutes = other.DurationMinutes,
                        Status = other.Status
                    })
                    .ToList();
            }
        }

        return View(model);
    }

    [HttpPost("Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(ModerationActionFormModel model)
        => await ProcessActionAsync(model.EventId, EventModerationActionType.Approved, null);

    [HttpPost("Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(ModerationActionFormModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Reason))
        {
            SetError("A reason is required when rejecting an event.");
            return RedirectToAction(nameof(Index));
        }
        return await ProcessActionAsync(model.EventId, EventModerationActionType.Rejected, model.Reason);
    }

    [HttpPost("Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(ModerationActionFormModel model)
    {
        var moderator = await GetCurrentUserInfoAsync();
        if (moderator == null) return Challenge();

        var guideEvent = await guide.GetEventForModerationAsync(model.EventId);
        if (guideEvent == null)
        {
            SetError("Event not found.");
            return RedirectToAction(nameof(Index), new { tab = EventStatus.Approved });
        }

        if (guideEvent.Status != EventStatus.Approved)
        {
            SetError("This event is not in an approved state.");
            return RedirectToAction(nameof(Index), new { tab = EventStatus.Approved });
        }

        await guide.WithdrawEventAsync(guideEvent);

        logger.LogInformation("Moderator {UserId} withdrew event '{Title}' ({EventId})",
            moderator.Id, guideEvent.Title, model.EventId);

        SetSuccess($"Event \"{guideEvent.Title}\" withdrawn.");
        return RedirectToAction(nameof(Index), new { tab = EventStatus.Approved });
    }

    [HttpPost("RequestEdit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEdit(ModerationActionFormModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Reason))
        {
            SetError("A reason is required when requesting edits.");
            return RedirectToAction(nameof(Index));
        }
        return await ProcessActionAsync(model.EventId, EventModerationActionType.ResubmitRequested, model.Reason);
    }

    // ─── Admin in-place edit (any event, any state, status preserved) ─────

    [HttpGet("{eventId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid eventId)
    {
        var guideEvent = await guide.GetEventForModerationAsync(eventId);
        if (guideEvent == null) return NotFound();

        var (eventSettings, tz) = await LoadEventSettingsAsync();
        if (eventSettings == null)
        {
            SetError("Event settings not configured.");
            return RedirectToAction(nameof(Index));
        }

        var localStart = ToLocalDateTime(guideEvent.StartAt, tz);
        var model = new AdminEventFormViewModel
        {
            Id = guideEvent.Id,
            IsCampEvent = guideEvent.CampId.HasValue,
            Status = guideEvent.Status,
            Title = guideEvent.Title,
            Description = guideEvent.Description,
            CategoryId = guideEvent.CategoryId,
            VenueId = guideEvent.GuideSharedVenueId,
            StartDate = localStart.Date,
            StartTime = localStart.TimeOfDay,
            IsAllDay = guideEvent.IsAllDay,
            DurationMinutes = guideEvent.DurationMinutes,
            LocationNote = guideEvent.LocationNote,
            Host = guideEvent.Host,
            IsRecurring = guideEvent.IsRecurring,
            RecurrenceDays = guideEvent.RecurrenceDays,
            PriorityRank = guideEvent.PriorityRank == 0 ? 1 : guideEvent.PriorityRank
        };

        if (guideEvent.CampId.HasValue)
            model.CampName = await ResolveCampNameAsync(guideEvent.CampId.Value, eventSettings);

        await PopulateAdminFormAsync(model, eventSettings);
        return View("AdminEventForm", model);
    }

    [HttpPost("{eventId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid eventId, AdminEventFormViewModel model)
    {
        var moderator = await GetCurrentUserInfoAsync();
        if (moderator == null) return Challenge();

        var guideEvent = await guide.GetEventForModerationAsync(eventId);
        if (guideEvent == null) return NotFound();

        // The event's own CampId — not the posted flag — is the source of truth
        // for which field set applies.
        var isCampEvent = guideEvent.CampId.HasValue;
        model.Id = eventId;
        model.IsCampEvent = isCampEvent;
        model.Status = guideEvent.Status;

        if (isCampEvent)
        {
            // Venue is irrelevant to camp events; PriorityRank's Range applies.
            ModelState.Remove(nameof(model.VenueId));
        }
        else
        {
            // PriorityRank is irrelevant to individual events; venue is required.
            ModelState.Remove(nameof(model.PriorityRank));
            if (!model.VenueId.HasValue || model.VenueId.Value == Guid.Empty)
                ModelState.AddModelError(nameof(model.VenueId), "Venue is required.");
        }

        var (eventSettings, tz) = await LoadEventSettingsAsync();
        if (eventSettings == null)
        {
            SetError("Event settings not configured.");
            return RedirectToAction(nameof(Index));
        }

        if (!ModelState.IsValid)
        {
            if (isCampEvent)
                model.CampName = await ResolveCampNameAsync(guideEvent.CampId!.Value, eventSettings);
            await PopulateAdminFormAsync(model, eventSettings);
            return View("AdminEventForm", model);
        }

        ApplyFormToEvent(guideEvent, model, isCampEvent, tz);

        await guide.AdminUpdateAsync(guideEvent, moderator.Id, model.Note);

        logger.LogInformation(
            "Moderator {UserId} edited event '{Title}' ({EventId}) in place; {Status} status preserved",
            moderator.Id, guideEvent.Title, eventId, guideEvent.Status);

        SetSuccess($"Event \"{guideEvent.Title}\" updated.");
        return RedirectToAction(nameof(Index), new { tab = guideEvent.Status });
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<(BurnSettingsInfo? eventSettings, DateTimeZone? tz)> LoadEventSettingsAsync()
    {
        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = guideSettings != null
            ? await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId)
            : null;
        return (eventSettings, GetTimeZone(eventSettings));
    }

    private async Task<string?> ResolveCampNameAsync(Guid campId, BurnSettingsInfo eventSettings)
    {
        var campsById = await LoadCampsByIdAsync(camps, eventSettings.GateOpeningDate.Year);
        var camp = campsById.GetValueOrDefault(campId);
        return camp?.Active?.Name ?? camp?.Slug;
    }

    private async Task PopulateAdminFormAsync(AdminEventFormViewModel model, BurnSettingsInfo burn)
    {
        var categories = (await guide.GetActiveCategoriesAsync())
            .Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList();
        // Keep the event's current category selectable even if it has since been
        // deactivated — otherwise editing an unrelated field would silently drop it.
        if (model.CategoryId != Guid.Empty && categories.All(c => c.Id != model.CategoryId))
        {
            var current = await guide.GetCategoryAsync(model.CategoryId);
            if (current != null)
                categories.Add(new CategoryOptionViewModel { Id = current.Id, Name = current.Name });
        }
        model.Categories = categories;

        if (!model.IsCampEvent)
        {
            var venues = (await guide.GetActiveVenuesAsync())
                .Select(v => new VenueOptionViewModel { Id = v.Id, Name = v.Name }).ToList();
            if (model.VenueId is { } venueId && venueId != Guid.Empty && venues.All(v => v.Id != venueId))
            {
                var current = await guide.GetVenueAsync(venueId);
                if (current != null)
                    venues.Add(new VenueOptionViewModel { Id = current.Id, Name = current.Name });
            }
            model.Venues = venues;
        }

        model.EventDays = BuildEventDayOptions(burn);
        model.TimeZoneId = burn.TimeZoneId;
    }

    // Maps the posted form fields onto the event. Camp events carry a priority
    // rank; individual events carry a venue and the all-day flag.
    private void ApplyFormToEvent(Event guideEvent, AdminEventFormViewModel model, bool isCampEvent, DateTimeZone? tz)
    {
        var (startTime, durationMinutes) = Event.ResolveAllDaySchedule(
            !isCampEvent && model.IsAllDay, model.StartTime, model.DurationMinutes);
        guideEvent.Title = model.Title;
        guideEvent.Description = model.Description;
        guideEvent.CategoryId = model.CategoryId;
        guideEvent.StartAt = ToInstant(model.StartDate.Add(startTime), tz);
        guideEvent.DurationMinutes = durationMinutes;
        guideEvent.LocationNote = model.LocationNote;
        guideEvent.Host = model.Host;
        guideEvent.IsRecurring = model.IsRecurring;
        guideEvent.RecurrenceDays = model.IsRecurring ? model.RecurrenceDays : null;
        if (isCampEvent)
            guideEvent.PriorityRank = model.PriorityRank;
        else
            guideEvent.GuideSharedVenueId = model.VenueId;
    }

    private async Task<IActionResult> ProcessActionAsync(Guid eventId, EventModerationActionType actionType, string? reason)
    {
        var moderator = await GetCurrentUserInfoAsync();
        if (moderator == null) return Challenge();

        var guideEvent = await guide.GetEventForModerationAsync(eventId);
        if (guideEvent == null)
        {
            SetError("Event not found.");
            return RedirectToAction(nameof(Index));
        }

        if (guideEvent.Status != EventStatus.Pending)
        {
            SetError("This event is not in a pending state.");
            return RedirectToAction(nameof(Index));
        }

        // The submitter-edit URL is Web routing turf; the decision notification
        // itself is part of ApplyModerationAsync's workflow.
        string? campSlug = null;
        if (guideEvent.CampId.HasValue)
        {
            var guideSettings = await guide.GetGuideSettingsAsync();
            var eventSettings = guideSettings != null
                ? await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId)
                : null;
            var campsById = await LoadCampsByIdAsync(camps, eventSettings?.GateOpeningDate.Year);
            campSlug = campsById.GetValueOrDefault(guideEvent.CampId.Value)?.Slug;
        }

        var editUrl = guideEvent.CampId.HasValue
            ? Url.Action("BarrioEdit", "Events", new { slug = campSlug, eventId }, Request.Scheme)!
            : Url.Action("Edit", "Events", new { eventId }, Request.Scheme)!;

        await guide.ApplyModerationAsync(eventId, moderator.Id, actionType, reason, editUrl);

        var actionLabel = actionType switch
        {
            EventModerationActionType.Approved => "approved",
            EventModerationActionType.Rejected => "rejected",
            EventModerationActionType.ResubmitRequested => "returned for edits",
            _ => "moderated"
        };

        logger.LogInformation("Moderator {UserId} {Action} event '{Title}' ({EventId})",
            moderator.Id, actionLabel, guideEvent.Title, eventId);

        SetSuccess($"Event \"{guideEvent.Title}\" {actionLabel}.");
        return RedirectToAction(nameof(Index));
    }

    private static ModerationEventRowViewModel BuildRow(
        EventInfo e,
        DateTimeZone? tz,
        IReadOnlyDictionary<Guid, CampInfo> campsById,
        IReadOnlyDictionary<Guid, UserInfo> submitterInfoById)
    {
        var submitter = submitterInfoById.GetValueOrDefault(e.SubmitterUserId);
        var submitterName = submitter?.BurnerName ?? submitter?.Email ?? "Unknown";

        var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
        var seasonName = camp?.Active?.Name;
        var campName = seasonName ?? camp?.Slug;

        return new ModerationEventRowViewModel
        {
            Id = e.Id,
            Title = e.Title,
            Description = e.Description,
            SubmitterName = submitterName,
            SubmitterUserId = e.SubmitterUserId,
            CampName = campName,
            CampSlug = camp?.Slug,
            VenueName = e.VenueName,
            CategoryName = e.CategoryName,
            StartAt = ToLocalDateTime(e.StartAt, tz),
            DurationMinutes = e.DurationMinutes,
            LocationNote = e.LocationNote,
            IsRecurring = e.IsRecurring,
            RecurrenceDays = e.RecurrenceDays,
            PriorityRank = e.PriorityRank,
            SubmittedAt = ToLocalDateTime(e.SubmittedAt, tz),
            Status = e.Status,
            History = e.ModerationHistory
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new ModerationHistoryItemViewModel
                {
                    ActorName = a.ActorUserId.ToString("N")[..8],
                    Action = a.Action,
                    Reason = a.Reason,
                    CreatedAt = ToLocalDateTime(a.CreatedAt, tz)
                }).ToList()
        };
    }


}
