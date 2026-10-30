namespace Humans.Teams.Contracts;

/// <summary>Teams' authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class TeamsPolicies
{
    /// <summary>Teams Admin, Board or Admin: the teams summary, team settings forms and Google sync preview.</summary>
    public const string Read = "TeamsRead";

    /// <summary>Teams Admin or Admin: create, edit and delete teams.</summary>
    public const string Manage = "TeamsManage";
}
