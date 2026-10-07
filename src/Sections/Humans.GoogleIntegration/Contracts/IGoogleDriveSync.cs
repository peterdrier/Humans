using Humans.Base.Enums;
using Humans.Base.Attributes;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Orchestrates Drive access for folders claimed through
/// <see cref="IGoogleDriveAccessSource"/> — including Teams' linked folders/files
/// and Workgroups. Unions every registered source's
/// expected access per folder, detects collisions (two sources claiming the
/// same folder), hydrates user IDs and applies user-state filtering
/// uniformly, then diffs against Google and applies changes through the
/// section's Drive permissions connector.
/// </summary>
/// <remarks>
/// Mirrors <see cref="IGoogleGroupSync"/>'s division of labour exactly, for
/// Drive folders instead of Groups.
/// </remarks>
public interface IGoogleDriveSync
{
    /// <summary>
    /// Reconciles every folder claimed by any registered source. Used by the
    /// daily <c>GoogleResourceReconciliationJob</c>.
    /// </summary>
    /// <param name="action">
    /// <see cref="SyncAction.Preview"/> computes the diff without mutating
    /// Google; <see cref="SyncAction.Execute"/> applies changes per the
    /// admin-configured <c>SyncSettings</c> mode (None / AddOnly / AddAndRemove)
    /// for <see cref="SyncServiceType.GoogleDrive"/>.
    /// </param>
    /// <param name="ct">Token used to cancel reconciliation.</param>
    /// <param name="resourceType">Optional filter for linked resources; null includes every source.</param>
    /// <param name="syncSource">Initiating action recorded in sync logs.</param>
    [ExternalWrite]
    Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleResourceType? resourceType = null,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    /// <summary>
    /// Reconciles one folder. Called by scoped on-demand sync requests
    /// (<see cref="IGoogleSyncService.RequestSyncAsync"/>).
    /// </summary>
    [ExternalWrite]
    Task<ResourceSyncDiff> ReconcileOneAsync(
        string folderId,
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);
}
