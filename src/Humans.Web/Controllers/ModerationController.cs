using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.EventGuide;
using Humans.Application.Interfaces.Repositories;
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
[Route("EventGuide/Moderate")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class ModerationController : HumansControllerBase
{
    private readonly IEventGuideService _guide;
    private readonly IEmailService _emailService;
    private readonly ILogger<ModerationController> _logger;

    public ModerationController(
        IEventGuideService guide,
        UserManager<User> userManager,
        IEmailService emailService,
        ILogger<ModerationController> logger)
        : base(userManager)
    {
        _guide = guide;
        _emailService = emailService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] GuideEventStatus? tab)
    {
        var activeTab = tab ?? GuideEventStatus.Pending;

        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var counts = await _guide.GetEventStatusCountsAsync();
        var events = await _guide.GetEventsByStatusAsync(activeTab);

        var model = new ModerationQueueViewModel
        {
            ActiveTab = activeTab,
            PendingCount = counts.GetValueOrDefault(GuideEventStatus.Pending),
            ApprovedCount = counts.GetValueOrDefault(GuideEventStatus.Approved),
            RejectedCount = counts.GetValueOrDefault(GuideEventStatus.Rejected),
            ResubmitRequestedCount = counts.GetValueOrDefault(GuideEventStatus.ResubmitRequested),
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
        => await ProcessActionAsync(model.EventId, ModerationActionType.Approved, null);

    [HttpPost("Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(ModerationActionFormModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Reason))
        {
            SetError("A reason is required when rejecting an event.");
            return RedirectToAction(nameof(Index));
        }
        return await ProcessActionAsync(model.EventId, ModerationActionType.Rejected, model.Reason);
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
        return await ProcessActionAsync(model.EventId, ModerationActionType.ResubmitRequested, model.Reason);
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<IActionResult> ProcessActionAsync(Guid eventId, ModerationActionType actionType, string? reason)
    {
        var moderator = await GetCurrentUserAsync();
        if (moderator == null) return Challenge();

        var guideEvent = await _guide.GetEventForModerationAsync(eventId);
        if (guideEvent == null)
        {
            SetError("Event not found.");
            return RedirectToAction(nameof(Index));
        }

        if (guideEvent.Status != GuideEventStatus.Pending)
        {
            SetError("This event is not in a pending state.");
            return RedirectToAction(nameof(Index));
        }

        await _guide.ApplyModerationAsync(eventId, moderator.Id, actionType, reason);

        var actionLabel = actionType switch
        {
            ModerationActionType.Approved => "approved",
            ModerationActionType.Rejected => "rejected",
            ModerationActionType.ResubmitRequested => "returned for edits",
            _ => "moderated"
        };

        _logger.LogInformation("Moderator {UserId} {Action} event '{Title}' ({EventId})",
            moderator.Id, actionLabel, guideEvent.Title, eventId);

        var submitterEmail = guideEvent.SubmitterUser.GetEffectiveEmail();
        var submitterName = guideEvent.SubmitterUser.Profile?.BurnerName
            ?? guideEvent.SubmitterUser.GetEffectiveEmail()
            ?? "Unknown";

        if (submitterEmail != null)
        {
            var editUrl = guideEvent.CampId.HasValue
                ? Url.Action("Edit", "CampEvents", new { slug = guideEvent.Camp?.Slug, eventId }, Request.Scheme)!
                : Url.Action("Edit", "EventGuide", new { eventId }, Request.Scheme)!;

            switch (actionType)
            {
                case ModerationActionType.Approved:
                    await _emailService.SendEventApprovedAsync(submitterEmail, submitterName, guideEvent.Title);
                    break;
                case ModerationActionType.Rejected:
                    await _emailService.SendEventRejectedAsync(submitterEmail, submitterName, guideEvent.Title, reason!, editUrl);
                    break;
                case ModerationActionType.ResubmitRequested:
                    await _emailService.SendEventResubmitRequestedAsync(submitterEmail, submitterName, guideEvent.Title, reason!, editUrl);
                    break;
            }
        }

        SetSuccess($"Event \"{guideEvent.Title}\" {actionLabel}.");
        return RedirectToAction(nameof(Index));
    }

    private static ModerationEventRowViewModel BuildRow(GuideEvent e, DateTimeZone? tz)
    {
        var submitterName = e.SubmitterUser.Profile?.BurnerName
            ?? e.SubmitterUser.GetEffectiveEmail()
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
            VenueName = e.GuideSharedVenue?.Name,
            CategoryName = e.Category.Name,
            StartAt = ToLocalDateTime(e.StartAt, tz),
            DurationMinutes = e.DurationMinutes,
            LocationNote = e.LocationNote,
            IsRecurring = e.IsRecurring,
            RecurrenceDays = e.RecurrenceDays,
            PriorityRank = e.PriorityRank,
            SubmittedAt = ToLocalDateTime(e.SubmittedAt, tz),
            Status = e.Status,
            History = e.ModerationActions
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new ModerationHistoryItemViewModel
                {
                    ActorName = a.ActorUser.Profile?.BurnerName ?? a.ActorUser.GetEffectiveEmail() ?? "Unknown",
                    Action = a.Action,
                    Reason = a.Reason,
                    CreatedAt = ToLocalDateTime(a.CreatedAt, tz)
                }).ToList()
        };
    }

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();
}
