using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Audit record of team join request state transitions.
/// </summary>
public class TeamJoinRequestStateHistory
{
    /// <summary>
    /// Unique identifier for the history record.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the join request.
    /// </summary>
    public Guid TeamJoinRequestId { get; init; }

    /// <summary>
    /// Navigation property to the join request.
    /// </summary>
    public TeamJoinRequest TeamJoinRequest { get; set; } = null!;

    /// <summary>
    /// The status after this transition.
    /// </summary>
    public TeamJoinRequestStatus Status { get; init; }

    /// <summary>
    /// When the state change occurred.
    /// </summary>
    public Instant ChangedAt { get; init; }

    /// <summary>
    /// ID of the user who made the change.
    /// </summary>
    public Guid ChangedByUserId { get; init; }

    /// <summary>
    /// Navigation property to the user who made the change.
    /// </summary>
    /// <remarks>
    /// Cross-domain nav into the Users section — will be removed per
    /// design-rules §6c once the User-entity nav strip follow-up lands.
    /// New callers resolve user data via <c>IUserService.GetByIdAsync</c>
    /// keyed on <see cref="ChangedByUserId"/>.
    /// </remarks>
    [Obsolete("Cross-domain nav; resolve via IUserService.GetByIdAsync(ChangedByUserId) instead. See design-rules §6c.")]
    public User ChangedByUser { get; set; } = null!;

    /// <summary>
    /// Optional notes about the state change.
    /// </summary>
    public string? Notes { get; init; }
}
