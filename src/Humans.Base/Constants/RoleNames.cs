
namespace Humans.Base.Constants;

/// <summary>
/// The two system-wide roles. Every other role belongs to a section (<c>UsersRoles</c>, <c>TeamsRoles</c>, …) and is
/// declared through <see cref="Interfaces.ISectionRoles"/>; Auth's <c>RoleCatalog</c> lists them all.
/// </summary>
public static class RoleNames
{
    public const string Admin = "Admin";

    public const string Board = "Board";
}
