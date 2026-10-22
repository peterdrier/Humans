using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Contracts;

/// <summary>
/// An annotation: it does not gate Volunteer admission. Reject and suspend are the kick-out levers, and their
/// state is Users' <c>UserInfo.State</c>.
/// </summary>
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
    public static ConsentReviewInfo None(Guid userId) =>
        new(userId, null, null, null, null, false, null, null, null);

    public bool IsRejected => RejectedAt is not null;
}

/// <summary>Shared by the queue, its nav pill, the notification meter and the dashboards so they can't drift.</summary>
public static class ConsentReviewPredicates
{
    /// <summary>Active (which excludes merged/deleted tombstones), named, and not yet approved.</summary>
    public static bool NeedsConsentReview(this UserInfo user, ConsentReviewInfo review)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(review);
        return user.IsActive && user.HasRequiredNameFields && !review.IsApproved;
    }

    /// <summary>
    /// Unresolved Flagged check. Excludes rejected members (Clear is blocked on them, so they'd be unresolvable)
    /// and merged/deleted tombstones.
    /// </summary>
    public static bool IsConsentCheckFlagged(this UserInfo user, ConsentReviewInfo review)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(review);
        return review.Status == ConsentCheckStatus.Flagged && !review.IsRejected && !user.IsTombstone;
    }
}
