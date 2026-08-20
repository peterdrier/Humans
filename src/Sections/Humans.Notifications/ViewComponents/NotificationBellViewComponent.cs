using Humans.Notifications.Services;
using Humans.Notifications.Models;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Humans.Notifications.ViewComponents;

internal sealed class NotificationBellViewComponent(INotificationInboxService notificationInboxService)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        // Was gated by Shell's `@if (User.Identity?.IsAuthenticated == true)` around the
        // by-name invocation; the chrome slot invokes unconditionally, so gate here instead.
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        var userId = GetUserId();
        if (userId is null)
            return View(new NotificationBadgeViewModel());

        var (actionableCount, informationalCount) = await notificationInboxService.GetUnreadBadgeCountsAsync(userId.Value);

        return View(new NotificationBadgeViewModel
        {
            ActionableUnreadCount = actionableCount,
            InformationalUnreadCount = informationalCount,
        });
    }

    private Guid? GetUserId()
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
