using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Users;

/// <summary>
/// Users' admin sidebar group: member admin, then the debug list and the temporary backfills.
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Users", [
            new("Humans", "UsersAdmin", "AdminList", null, null, "fa-solid fa-users",    PolicyNames.HumanAdminBoardOrAdmin, Weight: 0),
            new("Roles",  "UsersAdmin", "Roles",     null, null, "fa-solid fa-id-badge", PolicyNames.HumanAdminBoardOrAdmin, Weight: 10),
            new("Account merges", "UsersAdminAccountMerges", "Index", null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly, Weight: 30),
            new("Email problems", "ProfileAdmin", "EmailProblems", null, null, "fa-solid fa-envelope-circle-check", PolicyNames.AdminOnly, Weight: 40),
            // Read-only member-base segmentation stats (accounts × ticket × profile),
            // not a messaging tool.
            new("Audience segmentation", "UsersAdmin", "Audience", null, null, "fa-solid fa-chart-pie", PolicyNames.AdminOnly, Weight: 50),
            new("All users (debug)", "UsersAdminDebug", "Index", null, null, "fa-solid fa-bug-slash", PolicyNames.AdminOnly, Weight: 60),
            // Temporary one-off backfills.
            new("Picture migration",     "ProfilePictureMigrationAdmin", "Index", null, null, "fa-solid fa-image",     PolicyNames.AdminOnly, Weight: 70),
            new("Stub profile backfill", "ProfileBackfillAdmin",         "Index", null, null, "fa-solid fa-user-plus", PolicyNames.AdminOnly, Weight: 80),
            new("User name backfill",    "UserNameBackfillAdmin",        "Index", null, null, "fa-solid fa-signature", PolicyNames.AdminOnly, Weight: 90)
        ])
    ];
}
