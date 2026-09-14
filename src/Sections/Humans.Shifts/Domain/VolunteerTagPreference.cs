namespace Humans.Shifts.Domain;

/// <summary>
/// Links a volunteer to a ShiftTag they're interested in.
/// Used for personalized shift recommendations.
/// </summary>
internal sealed class VolunteerTagPreference
{
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the volunteer. Settable so the account-merge fold
    /// (<c>IShiftManagementService.ReassignProfilesAndTagPrefsToUserAsync</c>)
    /// can re-FK rows from a source user to the merge target.
    /// </summary>
    public Guid UserId { get; set; }

    public Guid ShiftTagId { get; init; }

    public ShiftTag ShiftTag { get; set; } = null!;
}
