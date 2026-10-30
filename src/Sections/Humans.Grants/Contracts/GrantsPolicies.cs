namespace Humans.Grants.Contracts;

/// <summary>Grants' authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class GrantsPolicies
{
    /// <summary>Grants Admin, Board or Admin: every program and its grants.</summary>
    public const string Read = "GrantsRead";

    /// <summary>Grants Admin or Admin: create and edit programs.</summary>
    public const string Manage = "GrantsManage";
}
