using Humans.Application.Interfaces.Notifications;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Humans.Web.ViewComponents;

public class NotificationBellViewComponent(INotificationInboxService notificationInboxService)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
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
