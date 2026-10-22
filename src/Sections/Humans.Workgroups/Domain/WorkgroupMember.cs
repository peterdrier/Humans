using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>Leaving is a soft <see cref="LeftAt"/>: the roster shows current members, the log keeps the history.</summary>
internal sealed class WorkgroupMember
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    public Guid UserId { get; set; }

    public WorkgroupMemberRole Role { get; set; }

    public Instant JoinedAt { get; set; }

    public Instant? LeftAt { get; set; }

    /// <summary>
    /// A pending row is not a member: no member work, no Drive access, no roster slot. Nullable so the column adds no
    /// physical default; null reads as not pending.
    /// </summary>
    public bool? IsPending { get; set; }

    public Workgroup Workgroup { get; set; } = null!;
}
