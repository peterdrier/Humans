using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Auth;

/// <summary>
/// Auth's contribution to the "Users" admin sidebar group: the role-assignment roster. The
/// group is merged by label with Users' own items.
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Users", [
            new("Roles", "RoleAssignments", "Roles", null, null, "fa-solid fa-id-badge", PolicyNames.HumanAdminBoardOrAdmin, Weight: 10)
        ])
    ];
}
