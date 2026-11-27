using Humans.EarlyEntry.Contracts;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Teams.Authorization;

/// <summary>Teams' declared permissions, and the requirements controllers authorize with on the team's owner.</summary>
internal static class TeamPermissionRequirements
{
    public static readonly Permission ManagePermission = new(TeamsPermissions.Manage,
        "Teams_Permission_Manage_Title", "Teams_Permission_Manage_Blurb", [TeamOwner.Kind], TopLevelOnly: false,
        AdminRoles: [TeamsRoles.TeamsAdmin], typeof(TeamsResource));

    public static readonly Permission EarlyEntryPermission = new(TeamsPermissions.EarlyEntry,
        "Teams_Permission_EarlyEntry_Title", "Teams_Permission_EarlyEntry_Blurb", [TeamOwner.Kind], TopLevelOnly: false,
        AdminRoles: [TeamsRoles.TeamsAdmin, EarlyEntryRoles.EarlyEntryAdmin], typeof(TeamsResource));

    public static readonly PermissionRequirement Manage = new(ManagePermission);

    public static readonly PermissionRequirement EarlyEntry = new(EarlyEntryPermission);
}
