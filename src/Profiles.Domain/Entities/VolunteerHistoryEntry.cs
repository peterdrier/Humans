using NodaTime;

namespace Profiles.Domain.Entities;

/// <summary>
/// A volunteer history entry documenting a member's involvement in events, roles, or camps.
/// </summary>
public class VolunteerHistoryEntry
{
    /// <summary>
    /// Unique identifier for the entry.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the profile.
    /// </summary>
    public Guid ProfileId { get; init; }

    /// <summary>
    /// Navigation property to the profile.
    /// </summary>
    public Profile Profile { get; set; } = null!;

    /// <summary>
    /// Date of the event/involvement. Users can enter full date or just use first of month.
    /// Displayed as "Mar'25" format.
    /// </summary>
    public LocalDate Date { get; set; }

    /// <summary>
    /// Name of the event, role, or camp.
    /// </summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the involvement.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the entry was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the entry was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }
}
