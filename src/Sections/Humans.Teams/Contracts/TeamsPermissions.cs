namespace Humans.Teams.Contracts;

/// <summary>
/// The permission keys Teams checks, declared by its <c>Section</c>. A holding on a team reaches its sub-teams.
/// </summary>
public static class TeamsPermissions
{
    /// <summary>Manage the team: members, join requests, roles and the page. Teams Admin holds it everywhere.</summary>
    public const string Manage = "Teams.Manage";

    /// <summary>Manage the team's early-entry grants. Teams Admin and Early Entry Admin hold it everywhere.</summary>
    public const string EarlyEntry = "Teams.EarlyEntry";
}
