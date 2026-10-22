using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Users;

/// <summary>Auth adds the role roster (Weight 10) to this group.</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Users", [
            new("Humans", "UsersAdmin", "AdminList", null, null, "fa-solid fa-users",    PolicyNames.HumanAdminBoardOrAdmin, Weight: 0),
            new("Account merges", "UsersAdminAccountMerges", "Index", null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly, Weight: 30),
            new("Email problems", "ProfileAdmin", "EmailProblems", null, null, "fa-solid fa-envelope-circle-check", PolicyNames.AdminOnly, Weight: 40),
            new("Audience segmentation", "UsersAdmin", "Audience", null, null, "fa-solid fa-chart-pie", PolicyNames.AdminOnly, Weight: 50)
        ])
    ];
}
