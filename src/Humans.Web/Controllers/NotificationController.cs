using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Notifications")]
public class NotificationController : HumansControllerBase
{
    private readonly INotificationInboxService _inboxService;
    private readonly INotificationMeterProvider _meterProvider;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public NotificationController(
        INotificationInboxService inboxService,
        UserManager<User> userManager,
        INotificationMeterProvider meterProvider,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _inboxService = inboxService;
        _meterProvider = meterProvider;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? search, string filter = "all", string tab = "unread")
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        // Resolved filter is incompatible with unread tab
        if (string.Equals(filter, "resolved", StringComparison.OrdinalIgnoreCase))
            tab = "all";

        var result = await _inboxService.GetInboxAsync(user.Id, search, filter, tab);

        var defaultActionLabel = _localizer["Notification_DefaultActionLabel"].Value;

        var meters = await _meterProvider.GetMetersForUserAsync(User);

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
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var result = await _inboxService.GetPopupAsync(user.Id);

        var defaultActionLabel = _localizer["Notification_DefaultActionLabel"].Value;

        var meters = await _meterProvider.GetMetersForUserAsync(User);

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
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var result = await _inboxService.ResolveAsync(id, user.Id);

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
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var result = await _inboxService.DismissAsync(id, user.Id);

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
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var result = await _inboxService.MarkReadAsync(id, user.Id);

        if (result.NotFound) return NotFound();

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

        await _inboxService.MarkAllReadAsync(user.Id);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("BulkResolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkResolve(List<Guid> selectedIds)
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        if (selectedIds.Count == 0)
            return RedirectToAction(nameof(Index));

        await _inboxService.BulkResolveAsync(selectedIds, user.Id);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("BulkDismiss")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDismiss(List<Guid> selectedIds)
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        if (selectedIds.Count == 0)
            return RedirectToAction(nameof(Index));

        await _inboxService.BulkDismissAsync(selectedIds, user.Id);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Ok();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("ClickThrough/{id}")]
    public async Task<IActionResult> ClickThrough(Guid id)
    {
        var (err, user) = await RequireCurrentUserAsync();
        if (err is not null) return err;

        var url = await _inboxService.ClickThroughAsync(id, user.Id);

        if (!string.IsNullOrEmpty(url) && Url.IsLocalUrl(url))
            return LocalRedirect(url);

        return RedirectToAction(nameof(Index));
    }

    private static NotificationRowViewModel MapToViewModel(NotificationRowDto dto, string defaultActionLabel)
    {
        return new NotificationRowViewModel
        {
            Id = dto.Id,
            Title = dto.Title,
            Body = dto.Body,
            ActionUrl = dto.ActionUrl,
            ActionLabel = dto.ActionLabel ?? defaultActionLabel,
            Priority = dto.Priority,
            Source = dto.Source,
            Class = dto.Class,
            TargetGroupName = dto.TargetGroupName,
            CreatedAt = dto.CreatedAt,
            IsRead = dto.IsRead,
            IsResolved = dto.IsResolved,
            ResolvedAt = dto.ResolvedAt,
            ResolvedByName = dto.ResolvedByName,
            RecipientInitials = dto.RecipientInitials,
            TotalRecipientCount = dto.TotalRecipientCount,
        };
    }
}
