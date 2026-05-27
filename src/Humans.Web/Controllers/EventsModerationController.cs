using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
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
    IEmailService emailService,
    IUserServiceRead users,
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
        var submitterInfoById = await LoadSubmittersAsync(users, events.Select(e => e.SubmitterUserId).Distinct());

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

                var endAt = evt.StartAt.Plus(Duration.FromMinutes(evt.DurationMinutes));
                row.DuplicateCandidates = allCampEvents
                    .Where(other => other.Id != evt.Id
                                 && other.CampId == evt.CampId
                                 && other.StartAt < endAt
                                 && evt.StartAt < other.StartAt.Plus(Duration.FromMinutes(other.DurationMinutes)))
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

    // ─── Helpers ──────────────────────────────────────────────────

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

        await guide.ApplyModerationAsync(eventId, moderator.Id, actionType, reason);

        var actionLabel = actionType switch
        {
            EventModerationActionType.Approved => "approved",
            EventModerationActionType.Rejected => "rejected",
            EventModerationActionType.ResubmitRequested => "returned for edits",
            _ => "moderated"
        };

        logger.LogInformation("Moderator {UserId} {Action} event '{Title}' ({EventId})",
            moderator.Id, actionLabel, guideEvent.Title, eventId);

        var submitterInfo = await users.GetUserInfoAsync(guideEvent.SubmitterUserId);
        var submitterEmail = submitterInfo?.Email;
        var submitterName = submitterInfo?.BurnerName ?? "Unknown";

        if (submitterEmail != null)
        {
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

            var lifecycleStatus = actionType switch
            {
                EventModerationActionType.Approved => (EventStatus?)EventStatus.Approved,
                EventModerationActionType.Rejected => EventStatus.Rejected,
                EventModerationActionType.ResubmitRequested => EventStatus.ResubmitRequested,
                _ => null
            };
            if (lifecycleStatus.HasValue)
            {
                await emailService.SendEventLifecycleNotificationAsync(
                    new EventLifecycleNotification(
                        NewStatus: lifecycleStatus.Value,
                        UserName: submitterName,
                        EventTitle: guideEvent.Title,
                        Reason: reason,
                        ActionUrl: editUrl),
                    submitterEmail);
            }
        }

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
