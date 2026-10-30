namespace Humans.Camps.Contracts;

/// <summary>Camps' authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class CampsPolicies
{
    /// <summary>Camps Admin, Board or Admin: the camps admin pages.</summary>
    public const string Read = "CampsRead";

    /// <summary>Camps Admin or Admin: season approvals, camp settings and camp roles.</summary>
    public const string Manage = "CampsManage";

    /// <summary>
    /// The read-only barrios compliance matrix: Camps Admin, Board, Admin, or a coordinator / management role-holder on
    /// any team, so team coordinators can see role staffing across barrios.
    /// </summary>
    public const string Compliance = "CampsCompliance";
}
