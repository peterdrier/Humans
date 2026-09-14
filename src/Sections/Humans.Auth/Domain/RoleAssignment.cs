using NodaTime;

namespace Humans.Auth.Domain;

/// <summary>
/// Represents a temporal role membership for a user.
/// Roles have a valid from/to period for historical tracking.
/// </summary>
internal sealed class RoleAssignment
{
    public Guid Id { get; init; }

    /// <summary>Bare cross-section id — no FK constraint, no navigation property.</summary>
    public Guid UserId { get; init; }

    public string RoleName { get; set; } = string.Empty;

    public Instant ValidFrom { get; init; }

    /// <summary>Null means open-ended: the assignment has not been ended.</summary>
    public Instant? ValidTo { get; set; }

    public string? Notes { get; set; }

    public Instant CreatedAt { get; init; }

    /// <summary>Bare cross-section id — no FK constraint, no navigation property.</summary>
    public Guid CreatedByUserId { get; init; }

    /// <summary>
    /// Determines if this role assignment is currently active.
    /// </summary>
    /// <param name="asOf">The point in time to check against.</param>
    /// <returns>True if the role is active at the specified time.</returns>
    public bool IsActive(Instant asOf)
    {
        if (asOf < ValidFrom)
        {
            return false;
        }

        if (ValidTo.HasValue && asOf >= ValidTo.Value)
        {
            return false;
        }

        return true;
    }
}
