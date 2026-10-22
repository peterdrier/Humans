namespace Humans.Users.Contracts;

/// <summary>Lower is more restrictive: access checks compare with &gt;=, so never reorder.</summary>
public enum ContactFieldVisibility
{
    BoardOnly = 0,

    CoordinatorsAndBoard = 1,

    /// <summary>Members who share at least one team with the owner.</summary>
    MyTeams = 2,

    AllActiveProfiles = 3
}
