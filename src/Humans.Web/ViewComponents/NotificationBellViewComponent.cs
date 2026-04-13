using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace Humans.Web.ViewComponents;

public class NotificationBellViewComponent : ViewComponent
{
    private readonly INotificationInboxService _notificationInboxService;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public NotificationBellViewComponent(INotificationInboxService notificationInboxService, IMemoryCache cache)
    {
        _notificationInboxService = notificationInboxService;
        _cache = cache;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = GetUserId();
        if (userId is null)
            return View(new NotificationBadgeViewModel());

        var cacheKey = CacheKeys.NotificationBadgeCounts(userId.Value);

        var model = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            var (actionableCount, informationalCount) = await _notificationInboxService.GetUnreadBadgeCountsAsync(userId.Value);

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
