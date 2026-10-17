using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>
/// One person's membership of one group. Joining files a pending request (<c>IsPending</c>)
/// that a coordinator approves; leaving is a soft <see cref="LeftAt"/> so the roster shows
/// current members while the log keeps the history.
/// </summary>
internal sealed class WorkgroupMember
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    /// <summary>Bare cross-section reference — no FK, no navigation.</summary>
    public Guid UserId { get; set; }

    public WorkgroupMemberRole Role { get; set; }

    public Instant JoinedAt { get; set; }

    /// <summary>Null while the person is a current member.</summary>
    public Instant? LeftAt { get; set; }

    /// <summary>
    /// True from the join request until a coordinator (or Board/Admin) approves it. A
    /// pending row is not a member: no member work, no Drive access, no roster slot.
    /// Nullable so the column adds no physical default; null reads as not pending.
    /// </summary>
    public bool? IsPending { get; set; }

    // Navigation properties (intra-section).

    public Workgroup Workgroup { get; set; } = null!;
}
