using Humans.Notifications.Services;
using Humans.Users.Contracts;
using Humans.Notifications.Models;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Humans.Notifications.ViewComponents;

internal sealed class NotificationBellViewComponent(INotificationInboxService notificationInboxService, IUserService users)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        // The chrome slot invokes this component for anonymous visitors too.
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        var userId = GetUserId();
        if (userId is null)
            return View(new NotificationBadgeViewModel());

        var user = await users.GetUserInfoAsync(userId.Value, HttpContext.RequestAborted);
        if (user is null)
            return View(new NotificationBadgeViewModel());

        var (actionableCount, informationalCount) = await notificationInboxService.GetUnreadBadgeCountsAsync(
            user.Id, HttpContext.RequestAborted);

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
