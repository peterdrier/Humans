using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Humans.Application;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Web.ViewComponents;

public class NavBadgesViewComponent : ViewComponent
{
    private readonly HumansDbContext _dbContext;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public NavBadgesViewComponent(HumansDbContext dbContext, IMemoryCache cache)
    {
        _dbContext = dbContext;
        _cache = cache;
    }

    public async Task<IViewComponentResult> InvokeAsync(string queue)
    {
        var counts = await _cache.GetOrCreateAsync(CacheKeys.NavBadgeCounts, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            var reviewCount = await _dbContext.Profiles
                .CountAsync(p => p.RejectedAt == null &&
                    (p.ConsentCheckStatus == ConsentCheckStatus.Pending ||
                     p.ConsentCheckStatus == ConsentCheckStatus.Flagged));

            var votingCount = await _dbContext.Applications
                .CountAsync(a => a.Status == ApplicationStatus.Submitted);

            return (Review: reviewCount, Voting: votingCount);
        });

        var count = string.Equals(queue, "review", StringComparison.OrdinalIgnoreCase)
            ? counts.Review
            : counts.Voting;

        return View(count);
    }
}
