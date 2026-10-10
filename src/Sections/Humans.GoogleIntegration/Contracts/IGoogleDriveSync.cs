using Humans.Base.Enums;
using Humans.Base.Attributes;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Orchestrates Drive access for resources claimed through
/// <see cref="IGoogleDriveAccessSource"/> — the source fan-out including Teams. Loads every registered source's
/// expected access per resource, detects collisions (two sources claiming the
/// same resource), hydrates user IDs and applies user-state filtering
/// uniformly, then diffs against Google and applies changes through the
/// section's Drive permissions connector.
/// </summary>
/// <remarks>
/// Mirrors <see cref="IGoogleGroupSync"/>'s division of labour exactly, for
/// Drive resources instead of Groups.
/// </remarks>
public interface IGoogleDriveSync
{
    /// <summary>
    /// Reconciles every resource claimed by any registered source. Used by the
    /// daily <c>GoogleResourceReconciliationJob</c>.
    /// </summary>
    /// <param name="action">
    /// <see cref="SyncAction.Preview"/> computes the diff without mutating
    /// Google; <see cref="SyncAction.Execute"/> applies changes per the
    /// admin-configured <c>SyncSettings</c> mode (None / AddOnly / AddAndRemove)
    /// for <see cref="SyncServiceType.GoogleDrive"/>.
    /// </param>
    /// <param name="ct">Token used to cancel reconciliation.</param>
    /// <param name="resourceType">Optional resource-type filter; unlinked source folders are DriveFolder.</param>
    /// <param name="syncSource">Trigger recorded on mutation logs.</param>
    [ExternalWrite]
    Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleResourceType? resourceType = null,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    /// <summary>
    /// Reconciles one Drive resource. Called by scoped on-demand sync requests
    /// (<see cref="IGoogleSyncService.RequestSyncAsync"/>).
    /// </summary>
    [ExternalWrite]
    Task<ResourceSyncDiff> ReconcileOneAsync(
        string folderId,
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);
}
