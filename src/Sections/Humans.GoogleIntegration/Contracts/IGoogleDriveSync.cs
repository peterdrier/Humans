using Humans.Base.Enums;
using Humans.Base.Attributes;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Orchestrates Drive access for every item claimed through
/// <see cref="IGoogleDriveAccessSource"/>. Combines compatible claims,
/// hydrates user IDs, applies eligibility policy and reconciles Google permissions.
/// Sections supply membership, resource provenance and permission policy.
/// </summary>
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
    /// <param name="syncSource">Initiating workflow recorded in the sync log.</param>
    [ExternalWrite]
    Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
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
