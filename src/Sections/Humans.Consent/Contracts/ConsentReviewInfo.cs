using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Contracts;

/// <summary>
/// A member's Consent Coordinator review: the consent-check annotation and the signup rejection's
/// reason, time and reviewer. <see cref="None"/> for a member nobody has reviewed yet.
/// </summary>
/// <remarks>
/// The review annotates; it does not gate Volunteer admission (name + required consents).
/// Reject and suspend are the kick-out levers, and their state is Users' <c>UserInfo.State</c>.
/// </remarks>
public sealed record ConsentReviewInfo(
    Guid UserId,
    ConsentCheckStatus? Status,
    Instant? CheckedAt,
    Guid? CheckedByUserId,
    string? Notes,
    bool IsApproved,
    string? RejectionReason,
    Instant? RejectedAt,
    Guid? RejectedByUserId)
{
    /// <summary>The review of a member with nothing recorded.</summary>
    public static ConsentReviewInfo None(Guid userId) =>
        new(userId, null, null, null, null, false, null, null, null);

    /// <summary>The signup was rejected.</summary>
    public bool IsRejected => RejectedAt is not null;
}

/// <summary>
/// The review-queue predicates, which need both the member (<see cref="UserInfo"/>) and their
/// review. Shared by the queue, its nav pill, the notification meter and the dashboards so they
/// cannot drift.
/// </summary>
public static class ConsentReviewPredicates
{
    /// <summary>In the Consent Coordinator's review queue: active (which excludes merged/deleted
    /// tombstones), named, and not yet approved.</summary>
    public static bool NeedsConsentReview(this UserInfo user, ConsentReviewInfo review)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(review);
        return user.IsActive && user.HasRequiredNameFields && !review.IsApproved;
    }

    /// <summary>
    /// Carries an unresolved Flagged consent check. Excludes rejected members (Clear is blocked on
    /// them, so they would be unresolvable in the queue) and merged/deleted tombstones, which are
    /// not live accounts left to review. Drives the /OnboardingReview flagged section.
    /// </summary>
    public static bool IsConsentCheckFlagged(this UserInfo user, ConsentReviewInfo review)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(review);
        return review.Status == ConsentCheckStatus.Flagged && !review.IsRejected && !user.IsTombstone;
    }
}
