namespace Humans.Roles.Contracts;

/// <summary>Roles' authorization policies, registered by its <c>Section</c>.</summary>
public static class RolesPolicies
{
    /// <summary>RolesAdmin, Board or Admin: the admin list of every owner's roles. Read-only.</summary>
    public const string Read = "RolesRead";
}
