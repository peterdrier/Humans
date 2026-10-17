using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Resolves a Drive folder's full path ("Shared Drive / Folder / Sub") by walking its parent
/// chain. Shared by the registered-resource path refresh and the access sync, so a folder
/// no section registered (a Workgroup's) is named the same way in emails and the sync log.
/// </summary>
internal static class DriveFolderPath
{
    public const string Separator = " / ";

    /// <summary>Null when any read fails or the walk never reaches a root, so a caller never shows a suffix as the path.</summary>
    public static async Task<string?> ResolveAsync(
        IGoogleDrivePermissionsClient drivePermissions, string fileId, ILogger logger, CancellationToken ct)
    {
        var segments = new List<string>();
        var currentId = fileId;
        var reachedRoot = false;
        const int maxDepth = 20;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            var fileResult = await drivePermissions.GetFileAsync(currentId, ct);
            if (fileResult?.File is null)
            {
                if (fileResult?.Error?.StatusCode == 404)
                    logger.LogWarning("Drive folder {GoogleId} not found", currentId);
                return null;
            }

            var file = fileResult.File;

            // If the file IS the Shared Drive root, fetch the drive name.
            if (!string.IsNullOrEmpty(file.DriveId)
                && string.Equals(currentId, file.DriveId, StringComparison.Ordinal))
            {
                var driveResult = await drivePermissions.GetSharedDriveAsync(file.DriveId, ct);
                var driveName = driveResult?.Drive?.Name ?? file.Name;
                if (driveName is null)
                    return null;
                segments.Add(driveName);
                reachedRoot = true;
                break;
            }

            if (file.Name is null)
                return null;
            segments.Add(file.Name);

            if (file.Parents is null || file.Parents.Count == 0)
            {
                reachedRoot = true;
                break;
            }

            currentId = file.Parents[0];
        }

        // A failed read or bounded walk must not overwrite a known full path with a suffix.
        if (!reachedRoot || segments.Count == 0)
            return null;

        segments.Reverse();
        return string.Join(Separator, segments);
    }
}
