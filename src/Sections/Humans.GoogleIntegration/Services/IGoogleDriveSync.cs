using Humans.Base.Enums;
using Humans.Base.Attributes;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Unions every source's claims (highest level per user), filters by user state, diffs against Google and applies the
/// changes. Same division of labour as <see cref="IGoogleGroupSync"/>, for Drive.
/// </summary>
internal interface IGoogleDriveSync
{
    /// <summary>
    /// <see cref="SyncAction.Execute"/> applies changes per the <see cref="SyncServiceType.GoogleDrive"/> sync mode.
    /// Unlinked source folders count as DriveFolder for <paramref name="resourceType"/>.
    /// </summary>
    [ExternalWrite]
    Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleResourceType? resourceType = null,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    [ExternalWrite]
    Task<ResourceSyncDiff> ReconcileOneAsync(
        string folderId,
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);
}
