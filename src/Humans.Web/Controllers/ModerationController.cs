using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.GuideModeratorOrAdmin)]
[Route("Events/Moderate")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class ModerationController : HumansControllerBase
{
    private readonly IEventService _guide;
    private readonly IEmailService _emailService;
    private readonly IUserService _users;
    private readonly ILogger<ModerationController> _logger;

    public ModerationController(
        IEventService guide,
        UserManager<User> userManager,
        IEmailService emailService,
        IUserService users,
        ILogger<ModerationController> logger)
        : base(userManager)
    {
        _guide = guide;
        _emailService = emailService;
        _users = users;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] EventStatus? tab)
    {
        var activeTab = tab ?? EventStatus.Pending;

        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var counts = await _guide.GetEventStatusCountsAsync();
        var events = await _guide.GetEventsByStatusAsync(activeTab);

        var model = new ModerationQueueViewModel
        {
            ActiveTab = activeTab,
            PendingCount = counts.GetValueOrDefault(EventStatus.Pending),
            ApprovedCount = counts.GetValueOrDefault(EventStatus.Approved),
            RejectedCount = counts.GetValueOrDefault(EventStatus.Rejected),
            ResubmitRequestedCount = counts.GetValueOrDefault(EventStatus.ResubmitRequested),
            TimeZoneId = guideSettings?.EventSettings?.TimeZoneId,
            Events = events.Select(e => BuildRow(e, tz)).ToList()
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

        var submitterEmail = guideEvent.SubmitterUser.Email;
        var submitterInfo = await _users.GetUserInfoAsync(guideEvent.SubmitterUserId);
        var submitterName = submitterInfo?.DisplayName ?? "Unknown";

        if (submitterEmail != null)
        {
            var editUrl = guideEvent.CampId.HasValue
                ? Url.Action("Edit", "CampEvents", new { slug = guideEvent.Camp?.Slug, eventId }, Request.Scheme)!
                : Url.Action("Edit", "EventGuide", new { eventId }, Request.Scheme)!;

            switch (actionType)
            {
                case EventModerationActionType.Approved:
                    await _emailService.SendEventApprovedAsync(submitterEmail, submitterName, guideEvent.Title);
                    break;
                case EventModerationActionType.Rejected:
                    await _emailService.SendEventRejectedAsync(submitterEmail, submitterName, guideEvent.Title, reason!, editUrl);
                    break;
                case EventModerationActionType.ResubmitRequested:
                    await _emailService.SendEventResubmitRequestedAsync(submitterEmail, submitterName, guideEvent.Title, reason!, editUrl);
                    break;
            }
        }

        SetSuccess($"Event \"{guideEvent.Title}\" {actionLabel}.");
        return RedirectToAction(nameof(Index));
    }

    private static ModerationEventRowViewModel BuildRow(Event e, DateTimeZone? tz)
    {
        var submitterName = e.SubmitterUser.Email
            ?? "Unknown";
        var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        var campName = campSeason?.Name ?? e.Camp?.Slug;

        return new ModerationEventRowViewModel
        {
            Id = e.Id,
            Title = e.Title,
            Description = e.Description,
            SubmitterName = submitterName,
            SubmitterUserId = e.SubmitterUserId,
            CampName = campName,
            CampSlug = e.Camp?.Slug,
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

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();
}
