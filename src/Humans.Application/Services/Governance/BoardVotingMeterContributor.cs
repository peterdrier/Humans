using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Domain.Constants;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Application.Services.Governance;

/// <summary>
/// Per-user meter: applications pending this board member's vote. Registered by the
/// Governance section (which owns the <c>applications</c> and <c>board_votes</c>
/// tables) per the push-model design in issue nobodies-collective/Humans#581.
/// </summary>
/// <remarks>
/// Self-caches per user under <see cref="CacheKeys.VotingBadge"/> with a 2-minute TTL.
/// The same cache key is read by <c>NavBadgesViewComponent</c> — keeping it unchanged
/// preserves shared cache warmth between the navbar badge and the notifications meter.
/// Invalidated by <see cref="IVotingBadgeCacheInvalidator"/> on vote submission.
/// </remarks>
public sealed class BoardVotingMeterContributor : INotificationMeterContributor
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IMemoryCache _cache;

    public BoardVotingMeterContributor(
        IApplicationDecisionService applicationDecisionService,
        IMemoryCache cache)
    {
        _applicationDecisionService = applicationDecisionService;
        _cache = cache;
    }

    public string Key => "BoardVoting";

    public NotificationMeterScope Scope => NotificationMeterScope.PerUser;

    public bool IsVisibleTo(ClaimsPrincipal user) => user.IsInRole(RoleNames.Board);

    public async Task<NotificationMeter?> BuildMeterAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userIdClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var boardMemberUserId))
            return null;

        var cacheKey = CacheKeys.VotingBadge(boardMemberUserId);
        var count = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await _applicationDecisionService
                .GetUnvotedApplicationCountAsync(boardMemberUserId, cancellationToken);
        });

        if (count <= 0) return null;

        return new NotificationMeter
        {
            Title = "Applications pending your vote",
            Count = count,
            ActionUrl = "/OnboardingReview/BoardVoting",
            Priority = 9,
        };
    }
}
