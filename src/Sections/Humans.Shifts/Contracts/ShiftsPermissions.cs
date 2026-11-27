namespace Humans.Shifts.Contracts;

/// <summary>
/// The permission keys Shifts checks, declared by its <c>Section</c>. Each is held on a team and reaches its sub-teams.
/// </summary>
public static class ShiftsPermissions
{
    /// <summary>Create and edit the team's rotas and shifts, and email its volunteers. Shifts Admin holds it everywhere.</summary>
    public const string Manage = "Shifts.Manage";

    /// <summary>Approve, refuse and voluntell the team's signups. Shifts Admin holds it everywhere.</summary>
    public const string Approve = "Shifts.Approve";
}
