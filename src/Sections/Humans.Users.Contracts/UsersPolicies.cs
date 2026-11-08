namespace Humans.Users.Contracts;

/// <summary>Users' authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class UsersPolicies
{
    /// <summary>Users Admin, Board or Admin: the human admin pages and the admin cards on a profile.</summary>
    public const string Read = "UsersRead";

    /// <summary>Users Admin or Admin: suspend, reject, role assignment and Workspace email changes.</summary>
    public const string Manage = "UsersManage";

    /// <summary>
    /// Admin, Board or Shifts NoInfo: may open one member's medical conditions through the audited reveal
    /// (<c>POST /Profile/{id}/Medical/Reveal</c>). Every reveal is audited, which is what lets Board in.
    /// </summary>
    public const string MedicalDataViewer = "UsersMedicalDataViewer";

    /// <summary>
    /// Users Admin who is neither Admin nor Board: drives the standalone "Humans" nav link for a viewer the admin shell
    /// would otherwise not lead to the human list.
    /// </summary>
    public const string UsersAdminOnly = "UsersAdminOnly";
}
