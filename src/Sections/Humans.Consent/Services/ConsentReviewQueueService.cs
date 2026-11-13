using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

internal sealed class ConsentReviewQueueService(
    IUserService userService,
    IConsentService consentService,
    IConsentWrite consentWrite,
    IGovernanceService governanceService,
    ILogger<ConsentReviewQueueService> logger) : IConsentReviewQueueService
{
    public async Task<ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default)
    {
        // An unresolved Flagged check stays in the queue even if an admin override set IsApproved, so a CC can
        // resolve it. Rejected members are excluded: Clear is blocked on them, so the row couldn't be resolved.
        var all = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var reviews = await consentService.GetConsentReviewsAsync(ct);
        var (pending, flagged) = PartitionReviewQueue(all, reviews);

        var allUserIds = flagged.Concat(pending).Select(u => u.Id).ToList();
        var pendingAppUserIds = await governanceService.GetUserIdsWithPendingApplicationAsync(allUserIds, ct);

        var consentProgress = new Dictionary<Guid, ConsentProgressInfo>();
        foreach (var userId in allUserIds)
        {
            var progress = await consentService.GetConsentProgressAsync(userId, ct);
            consentProgress[userId] = new ConsentProgressInfo(
                progress.RequiredCount - progress.PendingCount,
                progress.RequiredCount);
        }

        var tiers = await governanceService.GetMembershipTiersAsync(ct);

        return new ReviewQueueData(pending, flagged, pendingAppUserIds.ToHashSet(), consentProgress, tiers, reviews);
    }

    // Bulk clear's eligibility is defined as "a row in the queue", so it partitions here too and the two can't
    // drift. Both callers depend on the order.
    private static (List<UserInfo> Pending, List<UserInfo> Flagged) PartitionReviewQueue(
        IEnumerable<UserInfo> all, IReadOnlyDictionary<Guid, ConsentReviewInfo> reviews)
    {
        ConsentReviewInfo ReviewOf(UserInfo u) => reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None(u.Id);

        var flagged = all
            .Where(u => ReviewOf(u).IsConsentCheckFlagged(u))
            .OrderBy(u => u.Profile!.CreatedAt)
            .ToList();

        var pending = all
            .Where(u => ReviewOf(u).NeedsConsentReview(u) && !ReviewOf(u).IsConsentCheckFlagged(u))
            .OrderBy(u => u.Profile!.CreatedAt)
            .ToList();

        return (pending, flagged);
    }

    public async Task<ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        var profile = info?.Profile;

        if (profile is null)
            return new ReviewDetailData(null, 0, 0, null);

        var review = await consentService.GetConsentReviewAsync(userId, ct);

        var progress = await consentService.GetConsentProgressAsync(userId, ct);

        var pendingApp = await governanceService.GetSubmittedApplicationForUserAsync(userId, ct);
        var tier = await governanceService.GetMembershipTierAsync(userId, ct);

        return new ReviewDetailData(
            new ReviewProfileDetail(
                info!.FirstName,
                info.LastName,
                profile.City,
                profile.CountryCode,
                tier,
                review.Status,
                review.Notes,
                profile.CreatedAt),
            progress.RequiredCount - progress.PendingCount,
            progress.RequiredCount,
            pendingApp?.Motivation);
    }

    public Task<OnboardingResult> ClearAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default) =>
        consentWrite.RecordConsentCheckAsync(userId, ConsentCheckStatus.Cleared, reviewerId, notes, ct);

    public async Task<int> BulkClearAsync(
        IReadOnlyCollection<Guid> userIds, Guid reviewerId, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return 0;

        // Not GetReviewQueueAsync: that builds the page's render payload, all discarded here.
        var selected = userIds.ToHashSet();
        var all = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var (pending, flagged) = PartitionReviewQueue(all, await consentService.GetConsentReviewsAsync(ct));
        var eligibleUserIds = pending
            .Concat(flagged)
            .Where(u => selected.Contains(u.Id))
            .Select(u => u.Id)
            .ToList();

        var cleared = 0;
        foreach (var userId in eligibleUserIds)
        {
            var result = await ClearAsync(userId, reviewerId, notes: null, ct);
            if (result.Success)
                cleared++;
            else
                logger.LogWarning("BulkClear: skipped user {UserId}: {ErrorKey}", userId, result.ErrorKey);
        }

        return cleared;
    }

    public Task<OnboardingResult> FlagAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default) =>
        consentWrite.RecordConsentCheckAsync(userId, ConsentCheckStatus.Flagged, reviewerId, notes, ct);
}
