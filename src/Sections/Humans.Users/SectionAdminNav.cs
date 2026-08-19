using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Users;

/// <summary>
/// Users' contribution to the "Members" (shared with Onboarding), "Diagnostics" (shared with
/// Debug) and "Temp" admin groups (nobodies-collective/Humans#1077).
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Members", [
            new("Humans", "UsersAdmin", "AdminList", null, null, "fa-solid fa-users",    PolicyNames.HumanAdminBoardOrAdmin, Weight: 0),
            new("Roles",  "UsersAdmin", "Roles",     null, null, "fa-solid fa-id-badge", PolicyNames.HumanAdminBoardOrAdmin, Weight: 10),
            new("Account merges", "UsersAdminAccountMerges", "Index", null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly, Weight: 30),
            new("Email problems", "ProfileAdmin", "EmailProblems", null, null, "fa-solid fa-envelope-circle-check", PolicyNames.AdminOnly, Weight: 40),
            // Read-only member-base segmentation stats (accounts × ticket × profile),
            // not a messaging tool.
            new("Audience segmentation", "UsersAdmin", "Audience", null, null, "fa-solid fa-chart-pie", PolicyNames.AdminOnly, Weight: 50)
        ], Weight: 10),
        new("Diagnostics", System: true, Items: [
            new("All users (debug)", "UsersAdminDebug", "Index", null, null, "fa-solid fa-bug-slash", PolicyNames.AdminOnly, Weight: 60)
        ], Weight: 140),
        new("Temp", System: true, Items: [
            new("Picture migration",     "ProfilePictureMigrationAdmin", "Index", null, null, "fa-solid fa-image",     PolicyNames.AdminOnly, Weight: 0),
            new("Stub profile backfill", "ProfileBackfillAdmin",         "Index", null, null, "fa-solid fa-user-plus", PolicyNames.AdminOnly, Weight: 10)
        ], Weight: 170)
    ];
}
