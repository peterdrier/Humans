using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>
/// One person's membership of one group. Joining is immediate (clause 3 standing
/// approval); leaving is a soft <see cref="LeftAt"/> so the roster shows current
/// members while the log keeps the history.
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

    // Navigation properties (intra-section).

    public Workgroup Workgroup { get; set; } = null!;
}
