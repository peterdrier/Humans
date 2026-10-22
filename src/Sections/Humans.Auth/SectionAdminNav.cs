using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Auth;

/// <summary>Merged by label into Users' "Users" admin group.</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Users", [
            new("Roles", "RoleAssignments", "Roles", null, null, "fa-solid fa-id-badge", PolicyNames.HumanAdminBoardOrAdmin, Weight: 10)
        ])
    ];
}
