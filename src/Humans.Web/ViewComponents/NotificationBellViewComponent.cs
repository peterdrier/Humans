using Humans.Application;
using Humans.Application.Interfaces.Notifications;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace Humans.Web.ViewComponents;

public class NotificationBellViewComponent(INotificationInboxService notificationInboxService, IMemoryCache cache)
    : ViewComponent
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = GetUserId();
        if (userId is null)
            return View(new NotificationBadgeViewModel());

        var cacheKey = CacheKeys.NotificationBadgeCounts(userId.Value);

        var model = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            var (actionableCount, informationalCount) = await notificationInboxService.GetUnreadBadgeCountsAsync(userId.Value);

            return new NotificationBadgeViewModel
            {
                ActionableUnreadCount = actionableCount,
                InformationalUnreadCount = informationalCount,
            };
        });

        return View(model!);
    }

    private Guid? GetUserId()
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
