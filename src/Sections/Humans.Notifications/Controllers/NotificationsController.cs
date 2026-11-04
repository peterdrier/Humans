using Humans.Notifications.Services;
using Humans.Base.Attributes;
using Humans.Base.Controllers;
using Humans.Notifications.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Notifications.Controllers;

[Authorize]
[Route("Notifications")]
[AllowBeforeActiveMembership]
internal sealed class NotificationsController(
    INotificationInboxService inboxService,
    IUserService userService,
    NotificationMeterProvider meterProvider,
    ILogger<NotificationsController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? search, string filter = "all", string tab = "unread")
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var result = await inboxService.GetInboxAsync(user.Id, search, filter, tab, HttpContext.RequestAborted);

        var meters = await meterProvider.GetMetersForUserAsync(User, HttpContext.RequestAborted, user.Id);

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
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var result = await inboxService.GetPopupAsync(user.Id, HttpContext.RequestAborted);

        var meters = await meterProvider.GetMetersForUserAsync(User, HttpContext.RequestAborted, user.Id);

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
    public Task<IActionResult> Resolve(Guid id) =>
        DecideAsync(id, inboxService.ResolveAsync);

    [HttpPost("Dismiss/{id}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Dismiss(Guid id) =>
        DecideAsync(id, inboxService.DismissAsync);

    private async Task<IActionResult> DecideAsync(
        Guid id, Func<Guid, Guid, CancellationToken, Task<NotificationActionResult>> decide)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var result = await decide(id, user.Id, CancellationToken.None);
        if (result.NotFound) return NotFound();
        if (result.Forbidden) return Forbid();
        return MutationResponse();
    }

    [HttpPost("MarkRead/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var result = await inboxService.MarkReadAsync(id, user.Id);

        if (result.NotFound) return NotFound();

        return MutationResponse();
    }

    [HttpPost("MarkAllRead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        await inboxService.MarkAllReadAsync(user.Id);

        return MutationResponse();
    }

    [HttpPost("BulkResolve")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkResolve(List<Guid> selectedIds) =>
        MutateSelectionAsync(selectedIds, "bulk-resolve", inboxService.BulkResolveAsync);

    [HttpPost("BulkDismiss")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkDismiss(List<Guid> selectedIds) =>
        MutateSelectionAsync(selectedIds, "bulk-dismiss", inboxService.BulkDismissAsync);

    private async Task<IActionResult> MutateSelectionAsync(
        List<Guid> selectedIds, string operation, Func<List<Guid>, Guid, CancellationToken, Task> mutate)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed notification {Operation} selection for user {UserId}", operation, user.Id);
            return BadRequest(ModelState);
        }

        if (selectedIds.Count == 0)
            return RedirectToAction(nameof(Index));

        await mutate(selectedIds, user.Id, CancellationToken.None);
        return MutationResponse();
    }

    private IActionResult MutationResponse() =>
        Request.Headers.XRequestedWith == "XMLHttpRequest" ? Ok() : RedirectToAction(nameof(Index));

    [HttpGet("ClickThrough/{id}")]
    public async Task<IActionResult> ClickThrough(Guid id)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var url = await inboxService.ClickThroughAsync(id, user.Id);

        if (url is null) return NotFound();

        if (Url.IsLocalUrl(url))
            return LocalRedirect(url);

        return RedirectToAction(nameof(Index));
    }
}
