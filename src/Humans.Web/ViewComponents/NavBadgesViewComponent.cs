using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Humans.Application;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Onboarding;
using Humans.Domain.Constants;

namespace Humans.Web.ViewComponents;

public class NavBadgesViewComponent : ViewComponent
{
    private readonly IOnboardingService _onboardingService;
    private readonly IFeedbackService _feedbackService;
    private readonly IIssuesService _issuesService;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public NavBadgesViewComponent(
        IOnboardingService onboardingService,
        IFeedbackService feedbackService,
        IIssuesService issuesService,
        IMemoryCache cache)
    {
        _onboardingService = onboardingService;
        _feedbackService = feedbackService;
        _issuesService = issuesService;
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
        else if (string.Equals(queue, "issues", StringComparison.OrdinalIgnoreCase))
        {
            count = await GetPerUserIssuesCountAsync();
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

    /// <summary>
    /// Per-user count of issues actionable by the viewer:
    /// Open + Triage in sections their roles own, plus their own non-terminal
    /// issues. Admins see the global open count. Claims-first role lookup —
    /// see coding-rules.md. Caching + per-user invalidation live in
    /// <c>IssuesService</c> per <c>memory/code/viewcomponent-no-cache.md</c>.
    /// </summary>
    private async Task<int> GetPerUserIssuesCountAsync()
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var currentUserId))
            return 0;

        var roles = UserClaimsPrincipal.Claims
            .Where(c => string.Equals(c.Type, ClaimTypes.Role, StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToList();
        var isAdmin = UserClaimsPrincipal.IsInRole(RoleNames.Admin);

        return await _issuesService.GetActionableCountForViewerAsync(
            currentUserId, roles, isAdmin);
    }
}
