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

    /// <summary>
    /// Active (which excludes merged/deleted tombstones), a real person (not Users' <c>IsFake</c>), named, and not yet
    /// approved. Shared by the queue, its nav pill, the notification meter and the dashboards so they can't drift.
    /// </summary>
    public bool NeedsConsentReview(UserInfo user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsActive && !user.IsFake && user.HasRequiredNameFields && !IsApproved;
    }

    /// <summary>
    /// Unresolved Flagged check. Excludes rejected members (Clear is blocked on them, so they'd be unresolvable)
    /// merged/deleted tombstones, and fake accounts.
    /// </summary>
    public bool IsConsentCheckFlagged(UserInfo user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Status == ConsentCheckStatus.Flagged && !IsRejected && !user.IsTombstone && !user.IsFake;
    }
}
