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
                .CountAsync(p => !p.IsApproved && p.RejectedAt == null);

            var votingCount = await _dbContext.Applications
                .CountAsync(a => a.Status == ApplicationStatus.Submitted);

            var feedbackCount = await _dbContext.FeedbackReports
                .Where(f => f.Status != FeedbackStatus.Resolved && f.Status != FeedbackStatus.WontFix)
                .CountAsync(f =>
                    (f.Status == FeedbackStatus.Open && f.LastAdminMessageAt == null) ||
                    (f.LastReporterMessageAt != null && (f.LastAdminMessageAt == null || f.LastReporterMessageAt > f.LastAdminMessageAt)));

            return (Review: reviewCount, Voting: votingCount, Feedback: feedbackCount);
        });

        var count = string.Equals(queue, "review", StringComparison.OrdinalIgnoreCase)
            ? counts.Review
            : string.Equals(queue, "feedback", StringComparison.OrdinalIgnoreCase)
            ? counts.Feedback
            : counts.Voting;

        return View(count);
    }
}
