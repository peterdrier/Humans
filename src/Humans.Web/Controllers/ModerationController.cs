using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Web.Filters;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.GuideModeratorOrAdmin)]
[Route("EventGuide/Moderate")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class ModerationController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IEmailService _emailService;
    private readonly ILogger<ModerationController> _logger;

    public ModerationController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        IEmailService emailService,
        ILogger<ModerationController> logger)
        : base(userManager)
    {
        _dbContext = dbContext;
        _clock = clock;
        _emailService = emailService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] GuideEventStatus? tab)
    {
        var activeTab = tab ?? GuideEventStatus.Pending;

        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        // Get counts for all tabs
        var allEvents = await _dbContext.GuideEvents
            .Where(e => e.Status == GuideEventStatus.Pending
                     || e.Status == GuideEventStatus.Approved
                     || e.Status == GuideEventStatus.Rejected
                     || e.Status == GuideEventStatus.ResubmitRequested)
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        // Get events for the active tab
        var query = _dbContext.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Include(e => e.Camp).ThenInclude(c => c!.Seasons)
            .Include(e => e.GuideSharedVenue)
            .Include(e => e.ModerationActions).ThenInclude(a => a.ActorUser).ThenInclude(u => u.Profile)
            .Where(e => e.Status == activeTab);

        // Pending: oldest first; others: newest first
        query = activeTab == GuideEventStatus.Pending
            ? query.OrderBy(e => e.SubmittedAt)
            : query.OrderByDescending(e => e.SubmittedAt);

        var events = await query.ToListAsync();

        var model = new ModerationQueueViewModel
        {
            ActiveTab = activeTab,
            PendingCount = allEvents.FirstOrDefault(g => g.Status == GuideEventStatus.Pending)?.Count ?? 0,
            ApprovedCount = allEvents.FirstOrDefault(g => g.Status == GuideEventStatus.Approved)?.Count ?? 0,
            RejectedCount = allEvents.FirstOrDefault(g => g.Status == GuideEventStatus.Rejected)?.Count ?? 0,
            ResubmitRequestedCount = allEvents.FirstOrDefault(g => g.Status == GuideEventStatus.ResubmitRequested)?.Count ?? 0,
            TimeZoneId = guideSettings?.EventSettings?.TimeZoneId,
            Events = events.Select(e => BuildRow(e, tz)).ToList()
        };

        // Duplicate detection: for camp events, find time-overlapping events from the same camp
        var campEvents = events.Where(e => e.CampId.HasValue).ToList();
        if (campEvents.Count > 0)
        {
            // Load all pending/approved camp events for overlap checking
            var allCampEvents = await _dbContext.GuideEvents
                .Where(e => e.CampId != null &&
                            (e.Status == GuideEventStatus.Pending || e.Status == GuideEventStatus.Approved))
                .Select(e => new { e.Id, e.CampId, e.Title, e.StartAt, e.DurationMinutes, e.Status })
                .ToListAsync();

            foreach (var row in model.Events)
            {
                var evt = campEvents.FirstOrDefault(e => e.Id == row.Id);
                if (evt?.CampId == null) continue;

                var endAt = evt.StartAt.Plus(Duration.FromMinutes(evt.DurationMinutes));
                var overlaps = allCampEvents
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

                row.DuplicateCandidates = overlaps;
            }
        }

        return View(model);
    }

    [HttpPost("Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(ModerationActionFormModel model)
    {
        return await ProcessActionAsync(model.EventId, ModerationActionType.Approved, null);
    }

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
        var user = await GetCurrentUserAsync();
        if (user == null) return Challenge();

        var guideEvent = await _dbContext.GuideEvents
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Include(e => e.Camp)
            .FirstOrDefaultAsync(e => e.Id == eventId);

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

        guideEvent.ApplyModerationAction(actionType, _clock);

        var moderationAction = new ModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = eventId,
            ActorUserId = user.Id,
            Action = actionType,
            Reason = reason,
            CreatedAt = _clock.GetCurrentInstant()
        };

        _dbContext.ModerationActions.Add(moderationAction);
        await _dbContext.SaveChangesAsync();

        var actionLabel = actionType switch
        {
            ModerationActionType.Approved => "approved",
            ModerationActionType.Rejected => "rejected",
            ModerationActionType.ResubmitRequested => "returned for edits",
            _ => "moderated"
        };

        _logger.LogInformation("Moderator {UserId} {Action} event '{Title}' ({EventId})",
            user.Id, actionLabel, guideEvent.Title, eventId);

        // Queue email notification to submitter
        var submitterEmail = guideEvent.SubmitterUser.Email;
        var submitterName = guideEvent.SubmitterUser.Profile?.BurnerName ?? submitterEmail ?? "Unknown";
        if (submitterEmail != null)
        {
            // Build edit URL based on whether this is a camp or individual event
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
        var submitterName = e.SubmitterUser.Profile?.BurnerName ?? e.SubmitterUser.Email ?? "Unknown";
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
                    ActorName = a.ActorUser.Profile?.BurnerName ?? a.ActorUser.Email ?? "Unknown",
                    Action = a.Action,
                    Reason = a.Reason,
                    CreatedAt = ToLocalDateTime(a.CreatedAt, tz)
                }).ToList()
        };
    }

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
    {
        if (tz == null)
            return instant.ToDateTimeUtc();
        return instant.InZone(tz).ToDateTimeUnspecified();
    }
}
