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

    /// <summary>
    /// Null when any read fails or the walk never reaches a root, so a caller never shows a suffix as the path.
    /// A Shared Drive's root is the drive itself: its id is the drive id, Drive reports it as an anonymous
    /// folder (named "Drive", no parents) and the real name lives only on <c>drives.get</c>. That name is
    /// never guessed from the folder read — a failed <c>drives.get</c> is a failed walk.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        IGoogleDrivePermissionsClient drivePermissions, string fileId, ILogger logger, CancellationToken ct)
    {
        var segments = new List<string>();
        var currentId = fileId;
        string? knownDriveId = null;
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
            // Items under a drive all report it; the root may not, so carry it up from the leaf.
            knownDriveId = !string.IsNullOrEmpty(file.DriveId) ? file.DriveId : knownDriveId;
            var hasParent = file.Parents is { Count: > 0 };

            var isDriveRoot = knownDriveId is not null && string.Equals(currentId, knownDriveId, StringComparison.Ordinal);
            SharedDriveMetadataResult? driveResult = null;
            if (!isDriveRoot && !hasParent && knownDriveId is null)
            {
                // Parentless and unlabelled: either the root of a drive that omits its own driveId, or
                // an orphan. Only drives.get can tell them apart; "not a drive" keeps the folder's own name.
                driveResult = await drivePermissions.GetSharedDriveAsync(currentId, ct);
                isDriveRoot = driveResult?.Drive is not null;
                if (!isDriveRoot && driveResult?.Error is { StatusCode: not (400 or 403 or 404) })
                    return null;
            }

            if (isDriveRoot)
            {
                driveResult ??= await drivePermissions.GetSharedDriveAsync(currentId, ct);
                if (driveResult?.Drive?.Name is not { Length: > 0 } driveName)
                {
                    logger.LogWarning(
                        "Shared Drive {DriveId} name unavailable (HTTP {StatusCode}); not resolving the path",
                        currentId, driveResult?.Error?.StatusCode);
                    return null;
                }

                segments.Add(driveName);
                reachedRoot = true;
                break;
            }

            if (file.Name is null)
                return null;
            segments.Add(file.Name);

            if (!hasParent)
            {
                reachedRoot = true;
                break;
            }

            currentId = file.Parents![0];
        }

        // A failed read or bounded walk must not overwrite a known full path with a suffix.
        if (!reachedRoot || segments.Count == 0)
            return null;

        segments.Reverse();
        return string.Join(Separator, segments);
    }
}
