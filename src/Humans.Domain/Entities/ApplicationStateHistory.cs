using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Audit record of application state transitions.
/// </summary>
public class ApplicationStateHistory
{
    /// <summary>
    /// Unique identifier for the history record.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the application.
    /// </summary>
    public Guid ApplicationId { get; init; }

    /// <summary>
    /// Navigation property to the application.
    /// </summary>
    public Application Application { get; set; } = null!;

    /// <summary>
    /// The status after this transition.
    /// </summary>
    public ApplicationStatus Status { get; init; }

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
    public User ChangedByUser { get; set; } = null!;

    /// <summary>
    /// Optional notes about the state change.
    /// </summary>
    public string? Notes { get; init; }
}
