using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Notifications")]
public class NotificationsController(
    INotificationInboxService inboxService,
    IUserService userService,
    INotificationMeterProvider meterProvider,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? search, string filter = "all", string tab = "unread")
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        // Resolved filter is incompatible with unread tab
        if (string.Equals(filter, "resolved", StringComparison.OrdinalIgnoreCase))
            tab = "all";

        var result = await inboxService.GetInboxAsync(userId.Value, search, filter, tab);

        var defaultActionLabel = localizer["Notification_DefaultActionLabel"].Value;

        var meters = await meterProvider.GetMetersForUserAsync(User);

        return View(new NotificationInboxViewModel
        {
            NeedsAttention = result.NeedsAttention.Select(r => MapToViewModel(r, defaultActionLabel)).ToList(),
            Informational = result.Informational.Select(r => MapToViewModel(r, defaultActionLabel)).ToList(),
            Resolved = result.Resolved.Select(r => MapToViewModel(r, defaultActionLabel)).ToList(),
            Meters = meters,
            UnreadCount = result.UnreadCount,
            SearchTerm = search,
            ActiveFilter = filter,
            ActiveTab = tab,
        });
    }

    [HttpGet("Popup")]
    public async Task<IActionResult> GetPopup()
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var result = await inboxService.GetPopupAsync(userId.Value);

        var defaultActionLabel = localizer["Notification_DefaultActionLabel"].Value;

        var meters = await meterProvider.GetMetersForUserAsync(User);

        return PartialView("_NotificationPopup", new NotificationPopupViewModel
        {
            Actionable = result.Actionable.Select(r => MapToViewModel(r, defaultActionLabel)).ToList(),
            Informational = result.Informational.Select(r => MapToViewModel(r, defaultActionLabel)).ToList(),
            Meters = meters,
            ActionableCount = result.ActionableCount,
        });
    }

    [HttpPost("Resolve/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var result = await inboxService.ResolveAsync(id, userId.Value);

        if (result.NotFound) return NotFound();
        if (result.Forbidden) return Forbid();

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Dismiss/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var result = await inboxService.DismissAsync(id, userId.Value);

        if (result.NotFound) return NotFound();
        if (result.Forbidden) return StatusCode(403);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("MarkRead/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var result = await inboxService.MarkReadAsync(id, userId.Value);

        if (result.NotFound) return NotFound();

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("MarkAllRead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        await inboxService.MarkAllReadAsync(userId.Value);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("BulkResolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkResolve(List<Guid> selectedIds)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (selectedIds.Count == 0)
            return RedirectToAction(nameof(Index));

        await inboxService.BulkResolveAsync(selectedIds, userId.Value);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("BulkDismiss")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDismiss(List<Guid> selectedIds)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (selectedIds.Count == 0)
            return RedirectToAction(nameof(Index));

        await inboxService.BulkDismissAsync(selectedIds, userId.Value);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("ClickThrough/{id}")]
    public async Task<IActionResult> ClickThrough(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var url = await inboxService.ClickThroughAsync(id, userId.Value);

        if (url is null) return NotFound();

        if (Url.IsLocalUrl(url))
            return LocalRedirect(url);

        return RedirectToAction(nameof(Index));
    }

    private static NotificationRowViewModel MapToViewModel(NotificationRowDto dto, string defaultActionLabel)
    {
        return new NotificationRowViewModel
        {
            Id = dto.Id,
            Title = dto.Title,
            ActionUrl = dto.ActionUrl,
            ActionLabel = dto.ActionLabel ?? defaultActionLabel,
            Priority = dto.Priority,
            Source = dto.Source,
            Class = dto.Class,
            CreatedAt = dto.CreatedAt,
            IsRead = dto.IsRead,
            IsResolved = dto.IsResolved,
            ResolvedAt = dto.ResolvedAt,
            ResolvedByName = dto.ResolvedByName,
        };
    }
}
