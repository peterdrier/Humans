
namespace Humans.Base.Interfaces;

/// <summary>
/// The grantable roles a section owns. Each section declares its own; only <c>RoleNames.Admin</c> and
/// <c>RoleNames.Board</c> stay in Base, declared by Auth. Auth composes the contributions into its
/// <c>RoleCatalog</c>, which fails startup on a duplicate name.
/// </summary>
public interface ISectionRoles : ISectionContribution
{
    IReadOnlyList<RoleDefinition> Roles { get; }
}

/// <summary>One grantable role.</summary>
/// <param name="Name">The stored role name and the role claim value, <c>&lt;Section&gt;&lt;Role&gt;</c>.</param>
/// <param name="Title">Short label for pills, filters and the Staff page.</param>
/// <param name="Blurb">What the holders do, for the Staff page and the role-assignment form.</param>
/// <param name="Icon">Font Awesome classes.</param>
/// <param name="OpensAdminShell">False for a role whose pages live outside <c>/Admin</c>, so the ADMIN link stays hidden.</param>
/// <param name="FormerNames">
/// Names this role was stored under before a rename. Auth reads rows carrying one as this role until the
/// <c>auth.role-renames</c> data migration has rewritten them; a rename is a new constant plus a former name here.
/// </param>
public sealed record RoleDefinition(
    string Name,
    string Title,
    string Blurb,
    string Icon,
    bool OpensAdminShell = true,
    IReadOnlyList<string>? FormerNames = null)
{
    public IReadOnlyList<string> FormerNames { get; init; } = FormerNames ?? [];
}
