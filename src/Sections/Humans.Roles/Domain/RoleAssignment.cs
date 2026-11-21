using NodaTime;

namespace Humans.Roles.Domain;

/// <summary>A person in one slot of a role. Unassigning deletes the row; the audit log is the history.</summary>
internal sealed class RoleAssignment
{
    public Guid Id { get; init; }

    public Guid RoleId { get; init; }

    public Role Role { get; set; } = null!;

    public Guid UserId { get; set; }

    /// <summary>Zero-based.</summary>
    public int Slot { get; init; }

    public Instant AssignedAt { get; init; }
}
