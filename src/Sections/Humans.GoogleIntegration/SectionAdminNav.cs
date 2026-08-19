using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration;

/// <summary>GoogleIntegration's admin sidebar contribution — the "Google" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Google", System: true, Items: [
            new("Overview",              "Google", "Index",        null, null, "fa-brands fa-google",           PolicyNames.AdminOnly),
            new("Sync settings",         "Google", "SyncSettings", null, null, "fa-solid fa-sliders",           PolicyNames.AdminOnly),
            new("Resource sync",         "Google", "Sync",         null, null, "fa-solid fa-arrows-rotate",     PolicyNames.TeamsAdminBoardOrAdmin),
            new("All domain groups",     "Google", "AllGroups",    null, null, "fa-solid fa-globe",             PolicyNames.AdminOnly),
            new("Workspace accounts",    "Google", "Accounts",     null, null, "fa-solid fa-at",                PolicyNames.AdminOnly),
            new("Sync outbox",           "Google", "SyncOutbox",   null, null, "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("Sync results",          "Google", "SyncResults",  null, null, "fa-solid fa-list-check",        PolicyNames.AdminOnly),
            new("Group settings",        "Google", "GroupSettingsResults", null, null, "fa-solid fa-gears",     PolicyNames.AdminOnly),
            new("Email renames",         "Google", "EmailRenames", null, null, "fa-solid fa-right-left",        PolicyNames.AdminOnly),
            new("Email flag violations", "Google", "EmailFlagViolations", null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly)
        ], Weight: 110)
    ];
}
