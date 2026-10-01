using Humans.Notifications.Services;
using Humans.Base.Controllers;
using Humans.Notifications.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Notifications.Controllers;

[Authorize]
[Route("Notifications")]
internal sealed class NotificationsController(
    INotificationInboxService inboxService,
    IUserServiceRead userService,
    NotificationMeterProvider meterProvider) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? search, string filter = "all", string tab = "unread")
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var result = await inboxService.GetInboxAsync(userId.Value, search, filter, tab, HttpContext.RequestAborted);

        var meters = await meterProvider.GetMetersForUserAsync(User);

        return View(new NotificationInboxViewModel
        {
            NeedsAttention = result.NeedsAttention
                .OrderByDescending(r => r.CreatedAt).ToList(),
            Informational = result.Informational
                .OrderByDescending(r => r.CreatedAt).ToList(),
            Resolved = result.Resolved
                .OrderByDescending(r => r.CreatedAt).ToList(),
            Meters = meters,
            UnreadCount = result.UnreadCount,
            SearchTerm = search,
            ActiveFilter = filter,
            ActiveTab = result.EffectiveTab,
        });
    }

    [HttpGet("Popup")]
    public async Task<IActionResult> GetPopup()
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var result = await inboxService.GetPopupAsync(userId.Value, HttpContext.RequestAborted);

        var meters = await meterProvider.GetMetersForUserAsync(User);

        return PartialView("_NotificationPopup", new NotificationPopupViewModel
        {
            Actionable = result.Actionable
                .OrderByDescending(r => r.CreatedAt).ToList(),
            Informational = result.Informational
                .OrderByDescending(r => r.CreatedAt).ToList(),
            Meters = meters,
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
        if (result.Forbidden) return Forbid();

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
}
