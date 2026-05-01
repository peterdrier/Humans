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
    /// FK to the volunteer. Settable so the account-merge fold
    /// (<c>IShiftManagementService.ReassignProfilesAndTagPrefsToUserAsync</c>)
    /// can re-FK rows from a source user to the merge target.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// FK to the shift tag.
    /// </summary>
    public Guid ShiftTagId { get; init; }

    /// <summary>
    /// Navigation property to the shift tag.
    /// </summary>
    public ShiftTag ShiftTag { get; set; } = null!;
}
