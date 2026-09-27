using Humans.GoogleIntegration.Contracts;

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
}
