using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Represents a temporal role membership for a user.
/// Roles have a valid from/to period for historical tracking.
/// </summary>
public class RoleAssignment
{
    /// <summary>
    /// Unique identifier for the role assignment.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the user.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// The role name being assigned.
    /// </summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// When this role assignment becomes effective.
    /// </summary>
    public Instant ValidFrom { get; init; }

    /// <summary>
    /// When this role assignment expires. Null means no expiration.
    /// </summary>
    public Instant? ValidTo { get; set; }

    /// <summary>
    /// Notes about why this role was assigned.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When this role assignment record was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// ID of the user who created this assignment.
    /// </summary>
    public Guid CreatedByUserId { get; init; }

    /// <summary>
    /// Navigation property to the user who created this assignment.
    /// </summary>
    public User? CreatedByUser { get; set; }

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
