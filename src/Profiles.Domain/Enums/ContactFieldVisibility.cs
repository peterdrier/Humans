namespace Profiles.Domain.Enums;

/// <summary>
/// Visibility levels for contact fields.
/// Lower values are more restrictive (enables >= filtering).
/// </summary>
public enum ContactFieldVisibility
{
    /// <summary>
    /// Only visible to board members.
    /// </summary>
    BoardOnly = 0,

    /// <summary>
    /// Visible to team leads (metaleads) and board members.
    /// </summary>
    LeadsAndBoard = 1,

    /// <summary>
    /// Visible to members who share at least one team with the profile owner.
    /// </summary>
    MyTeams = 2,

    /// <summary>
    /// Visible to all active members of the organization.
    /// </summary>
    AllActiveProfiles = 3
}
