using Humans.Application;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Notifications")]
public class NotificationController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        IMemoryCache cache,
        IStringLocalizer<SharedResource> localizer,
        ILogger<NotificationController> logger)
        : base(userManager)
    {
        _dbContext = dbContext;
        _clock = clock;
        _cache = cache;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? search, string filter = "all", string tab = "unread")
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var query = _dbContext.NotificationRecipients
            .Where(nr => nr.UserId == user.Id)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.ResolvedByUser)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.Recipients)
                    .ThenInclude(r => r.User)
            .AsNoTracking();

        // Tab filter
        if (string.Equals(tab, "unread", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr => nr.Notification.ResolvedAt == null);
        }
        else
        {
            // All: unresolved + resolved within last 7 days
            query = query.Where(nr =>
                nr.Notification.ResolvedAt == null ||
                nr.Notification.ResolvedAt > cutoff);
        }

        // Filter pills
        if (string.Equals(filter, "action", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr => nr.Notification.Class == NotificationClass.Actionable);
        }
        else if (string.Equals(filter, "shifts", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr =>
                nr.Notification.Source == NotificationSource.ShiftCoverageGap ||
                nr.Notification.Source == NotificationSource.ShiftSignupChange);
        }
        else if (string.Equals(filter, "approvals", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr =>
                nr.Notification.Source == NotificationSource.ConsentReviewNeeded ||
                nr.Notification.Source == NotificationSource.ApplicationSubmitted);
        }
        else if (string.Equals(filter, "resolved", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr => nr.Notification.ResolvedAt != null);
        }

        // Search
        if (!string.IsNullOrWhiteSpace(search) && search.Trim().Length >= 2)
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(nr =>
                EF.Functions.ILike(nr.Notification.Title, term) ||
                (nr.Notification.Body != null && EF.Functions.ILike(nr.Notification.Body, term)));
        }

        var recipients = await query
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync();

        var needsAttention = new List<NotificationRowViewModel>();
        var informational = new List<NotificationRowViewModel>();
        var resolved = new List<NotificationRowViewModel>();

        foreach (var nr in recipients)
        {
            var vm = MapToRow(nr);
            if (vm.IsResolved)
                resolved.Add(vm);
            else if (nr.Notification.Class == NotificationClass.Actionable)
                needsAttention.Add(vm);
            else
                informational.Add(vm);
        }

        var unreadCount = recipients.Count(nr =>
            nr.Notification.ResolvedAt == null && nr.ReadAt == null);

        return View(new NotificationInboxViewModel
        {
            NeedsAttention = needsAttention,
            Informational = informational,
            Resolved = resolved,
            UnreadCount = unreadCount,
            SearchTerm = search,
            ActiveFilter = filter,
            ActiveTab = tab,
        });
    }

    [HttpGet("Popup")]
    public async Task<IActionResult> GetPopup()
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var recipients = await _dbContext.NotificationRecipients
            .Where(nr => nr.UserId == user.Id && nr.Notification.ResolvedAt == null)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.Recipients)
                    .ThenInclude(r => r.User)
            .AsNoTracking()
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync();

        var actionable = new List<NotificationRowViewModel>();
        var informational = new List<NotificationRowViewModel>();

        foreach (var nr in recipients)
        {
            var vm = MapToRow(nr);
            if (nr.Notification.Class == NotificationClass.Actionable)
                actionable.Add(vm);
            else
                informational.Add(vm);
        }

        return PartialView("_NotificationPopup", new NotificationPopupViewModel
        {
            Actionable = actionable,
            Informational = informational,
            ActionableCount = actionable.Count,
        });
    }

    [HttpPost("Resolve/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid id)
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var notification = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .FirstOrDefaultAsync(n => n.Id == id);

        if (notification is null)
            return NotFound();

        // Verify the user is a recipient
        if (!notification.Recipients.Any(r => r.UserId == user.Id))
            return Forbid();

        if (notification.ResolvedAt is null)
        {
            notification.ResolvedAt = _clock.GetCurrentInstant();
            notification.ResolvedByUserId = user.Id;
            await _dbContext.SaveChangesAsync();
            _cache.Remove(CacheKeys.NavBadgeCounts);
        }

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Dismiss/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(Guid id)
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var notification = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .FirstOrDefaultAsync(n => n.Id == id);

        if (notification is null)
            return NotFound();

        // Verify the user is a recipient
        if (!notification.Recipients.Any(r => r.UserId == user.Id))
            return Forbid();

        // Actionable notifications cannot be dismissed
        if (notification.Class == NotificationClass.Actionable)
            return StatusCode(403);

        if (notification.ResolvedAt is null)
        {
            notification.ResolvedAt = _clock.GetCurrentInstant();
            notification.ResolvedByUserId = user.Id;
            await _dbContext.SaveChangesAsync();
            _cache.Remove(CacheKeys.NavBadgeCounts);
        }

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("MarkRead/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var recipient = await _dbContext.NotificationRecipients
            .FirstOrDefaultAsync(nr => nr.NotificationId == id && nr.UserId == user.Id);

        if (recipient is null)
            return NotFound();

        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = _clock.GetCurrentInstant();
            await _dbContext.SaveChangesAsync();
        }

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("MarkAllRead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var now = _clock.GetCurrentInstant();

        var unread = await _dbContext.NotificationRecipients
            .Where(nr => nr.UserId == user.Id && nr.ReadAt == null)
            .ToListAsync();

        foreach (var nr in unread)
        {
            nr.ReadAt = now;
        }

        if (unread.Count > 0)
        {
            await _dbContext.SaveChangesAsync();
            _cache.Remove(CacheKeys.NavBadgeCounts);
        }

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("ClickThrough/{id}")]
    public async Task<IActionResult> ClickThrough(Guid id)
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var recipient = await _dbContext.NotificationRecipients
            .Include(nr => nr.Notification)
            .FirstOrDefaultAsync(nr => nr.NotificationId == id && nr.UserId == user.Id);

        if (recipient is null)
            return RedirectToAction(nameof(Index));

        // Mark as read on click-through
        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = _clock.GetCurrentInstant();
            await _dbContext.SaveChangesAsync();
        }

        var url = recipient.Notification.ActionUrl;
        if (!string.IsNullOrEmpty(url) && url.StartsWith('/'))
            return Redirect(url);

        return RedirectToAction(nameof(Index));
    }

    private static NotificationRowViewModel MapToRow(NotificationRecipient nr)
    {
        var n = nr.Notification;
        var allRecipients = n.Recipients?.ToList() ?? [];

        return new NotificationRowViewModel
        {
            Id = n.Id,
            Title = n.Title,
            Body = n.Body,
            ActionUrl = n.ActionUrl,
            ActionLabel = n.ActionLabel ?? "View \u2192",
            Priority = n.Priority,
            Source = n.Source,
            Class = n.Class,
            TargetGroupName = n.TargetGroupName,
            CreatedAt = n.CreatedAt.ToDateTimeUtc(),
            IsRead = nr.ReadAt is not null,
            IsResolved = n.ResolvedAt is not null,
            ResolvedAt = n.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = n.ResolvedByUser?.DisplayName,
            RecipientInitials = allRecipients
                .Take(3)
                .Select(r => GetInitials(r.User?.DisplayName))
                .ToList(),
            TotalRecipientCount = allRecipients.Count,
        };
    }

    private static string GetInitials(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "?";
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
            : parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
    }
}
