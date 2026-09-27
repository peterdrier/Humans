using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>Maps Google Drive API permission-role strings to the section's permission level.</summary>
internal static class DrivePermissionRoleMapper
{
    internal static DrivePermissionLevel? Parse(string? role) => role switch
    {
        "reader" => DrivePermissionLevel.Viewer,
        "commenter" => DrivePermissionLevel.Commenter,
        "writer" => DrivePermissionLevel.Contributor,
        "fileOrganizer" => DrivePermissionLevel.ContentManager,
        "organizer" => DrivePermissionLevel.Manager,
        _ => null
    };

    internal static bool IsAnyUserPermission(DrivePermission permission) =>
        string.Equals(permission.Type, "user", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(permission.EmailAddress)
        && !permission.EmailAddress.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase);
}
