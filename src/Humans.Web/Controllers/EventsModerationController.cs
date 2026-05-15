using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using static Humans.Web.Helpers.EventsLookupHelpers;
using static Humans.Web.Helpers.EventsTimeHelpers;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.EventsAdminOrAdmin)]
[Route("Events/Moderate")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class EventsModerationController : HumansControllerBase
{
    private readonly IEventService _guide;
    private readonly IEmailService _emailService;
    private readonly IUserService _users;
    private readonly ICampService _camps;
    private readonly ILogger<EventsModerationController> _logger;

    public EventsModerationController(
        IEventService guide,
        UserManager<User> userManager,
        IEmailService emailService,
        IUserService users,
        ICampService camps,
        ILogger<EventsModerationController> logger)
        : base(userManager)
    {
        _guide = guide;
        _emailService = emailService;
        _users = users;
        _camps = camps;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] EventStatus? tab)
    {
        var activeTab = tab ?? EventStatus.Pending;

        var guideSettings = await _guide.GetGuideSettingsAsync();
        var eventSettings = guideSettings != null
            ? await _guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId)
            : null;
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;

        var counts = await _guide.GetEventStatusCountsAsync();
        var events = await _guide.GetEventsByStatusAsync(activeTab);

        var campsById = await LoadCampsByIdAsync(_camps, eventSettings?.GateOpeningDate.Year);
        var submitterInfoById = await LoadSubmittersAsync(_users, events.Select(e => e.SubmitterUserId).Distinct());

        var model = new ModerationQueueViewModel
        {
            ActiveTab = activeTab,
            PendingCount = counts.GetValueOrDefault(EventStatus.Pending),
            ApprovedCount = counts.GetValueOrDefault(EventStatus.Approved),
            RejectedCount = counts.GetValueOrDefault(EventStatus.Rejected),
            ResubmitRequestedCount = counts.GetValueOrDefault(EventStatus.ResubmitRequested),
            TimeZoneId = eventSettings?.TimeZoneId,
            Events = events.Select(e => BuildRow(e, tz, campsById, submitterInfoById)).ToList()
        };

        // Duplicate detection for camp events
        var campEvents = events.Where(e => e.CampId.HasValue).ToList();
        if (campEvents.Count > 0)
        {
            var allCampEvents = await _guide.GetCampEventsForOverlapAsync();

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
        var moderator = await GetCurrentUserAsync();
        if (moderator == null) return Challenge();

        var guideEvent = await _guide.GetEventForModerationAsync(eventId);
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

        await _guide.ApplyModerationAsync(eventId, moderator.Id, actionType, reason);

        var actionLabel = actionType switch
        {
            EventModerationActionType.Approved => "approved",
            EventModerationActionType.Rejected => "rejected",
            EventModerationActionType.ResubmitRequested => "returned for edits",
            _ => "moderated"
        };

        _logger.LogInformation("Moderator {UserId} {Action} event '{Title}' ({EventId})",
            moderator.Id, actionLabel, guideEvent.Title, eventId);

        var submitterInfo = await _users.GetUserInfoAsync(guideEvent.SubmitterUserId);
        var submitterEmail = submitterInfo?.Email;
        var submitterName = submitterInfo?.DisplayName ?? "Unknown";

        if (submitterEmail != null)
        {
            string? campSlug = null;
            if (guideEvent.CampId.HasValue)
            {
                var guideSettings = await _guide.GetGuideSettingsAsync();
                var eventSettings = guideSettings != null
                    ? await _guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId)
                    : null;
                var campsById = await LoadCampsByIdAsync(_camps, eventSettings?.GateOpeningDate.Year);
                campSlug = campsById.GetValueOrDefault(guideEvent.CampId.Value)?.Slug;
            }

            var editUrl = guideEvent.CampId.HasValue
                ? Url.Action("Edit", "BarrioEvents", new { slug = campSlug, eventId }, Request.Scheme)!
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
                await _emailService.SendEventLifecycleNotificationAsync(
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
        Event e,
        DateTimeZone? tz,
        IReadOnlyDictionary<Guid, CampInfo> campsById,
        IReadOnlyDictionary<Guid, UserInfo> submitterInfoById)
    {
        var submitter = submitterInfoById.GetValueOrDefault(e.SubmitterUserId);
        var submitterName = submitter?.DisplayName ?? submitter?.Email ?? "Unknown";

        var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
        var seasonName = camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name;
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
            VenueName = e.EventVenue?.Name,
            CategoryName = e.Category.Name,
            StartAt = ToLocalDateTime(e.StartAt, tz),
            DurationMinutes = e.DurationMinutes,
            LocationNote = e.LocationNote,
            IsRecurring = e.IsRecurring,
            RecurrenceDays = e.RecurrenceDays,
            PriorityRank = e.PriorityRank,
            SubmittedAt = ToLocalDateTime(e.SubmittedAt, tz),
            Status = e.Status,
            History = e.EventModerationActions
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
