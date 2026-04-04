using Humans.Application;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace Humans.Web.ViewComponents;

public class NotificationBellViewComponent : ViewComponent
{
    private readonly HumansDbContext _dbContext;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public NotificationBellViewComponent(HumansDbContext dbContext, IMemoryCache cache)
    {
        _dbContext = dbContext;
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

            var actionableCount = await _dbContext.NotificationRecipients
                .CountAsync(nr => nr.UserId == userId.Value &&
                                  nr.ReadAt == null &&
                                  nr.Notification.ResolvedAt == null &&
                                  nr.Notification.Class == NotificationClass.Actionable);

            var informationalCount = await _dbContext.NotificationRecipients
                .CountAsync(nr => nr.UserId == userId.Value &&
                                  nr.ReadAt == null &&
                                  nr.Notification.ResolvedAt == null &&
                                  nr.Notification.Class == NotificationClass.Informational);

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
