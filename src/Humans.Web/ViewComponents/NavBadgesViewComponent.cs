using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Humans.Application;
using Humans.Application.Interfaces;

namespace Humans.Web.ViewComponents;

public class NavBadgesViewComponent : ViewComponent
{
    private readonly IOnboardingService _onboardingService;
    private readonly IFeedbackService _feedbackService;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public NavBadgesViewComponent(IOnboardingService onboardingService, IFeedbackService feedbackService, IMemoryCache cache)
    {
        _onboardingService = onboardingService;
        _feedbackService = feedbackService;
        _cache = cache;
    }

    public async Task<IViewComponentResult> InvokeAsync(string queue)
    {
        var counts = await _cache.GetOrCreateAsync(CacheKeys.NavBadgeCounts, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            var reviewCount = await _onboardingService.GetPendingReviewCountAsync();
            var feedbackCount = await _feedbackService.GetActionableCountAsync();

            return (Review: reviewCount, Feedback: feedbackCount);
        });

        int count;
        if (string.Equals(queue, "voting", StringComparison.OrdinalIgnoreCase))
        {
            count = await GetPerUserVotingCountAsync();
        }
        else if (string.Equals(queue, "review", StringComparison.OrdinalIgnoreCase))
        {
            count = counts.Review;
        }
        else
        {
            count = counts.Feedback;
        }

        return View(count);
    }

    private async Task<int> GetPerUserVotingCountAsync()
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var currentUserId))
            return 0;

        var cacheKey = CacheKeys.VotingBadge(currentUserId);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            return await _onboardingService.GetUnvotedApplicationCountAsync(currentUserId);
        });
    }
}
