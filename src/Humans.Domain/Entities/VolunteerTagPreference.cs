namespace Humans.Domain.Entities;

/// <summary>
/// Links a volunteer to a ShiftTag they're interested in.
/// Used for personalized shift recommendations.
/// </summary>
public class VolunteerTagPreference
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the volunteer.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// FK to the shift tag.
    /// </summary>
    public Guid ShiftTagId { get; init; }

    /// <summary>
    /// Navigation property to the volunteer.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Navigation property to the shift tag.
    /// </summary>
    public ShiftTag ShiftTag { get; set; } = null!;
}
