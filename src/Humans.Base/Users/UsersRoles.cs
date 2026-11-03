namespace Humans.Users.Contracts;

/// <summary>Users' grantable role. Declared to Auth by Users' <c>Section</c>.</summary>
public static class UsersRoles
{
    /// <summary>
    /// Users Admin: views the human admin pages, approves/suspends/rejects humans, provisions @nobodies.team
    /// accounts and manages role assignments. Stored as <c>HumanAdmin</c> before 2026-10.
    /// </summary>
    public const string UsersAdmin = "UsersAdmin";
}
