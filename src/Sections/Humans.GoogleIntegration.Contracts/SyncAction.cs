namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Whether a sync operation should preview changes or execute them.
/// Add/remove behavior is controlled by SyncSettings (Admin/SyncSettings),
/// enforced by the gateway methods in GoogleWorkspaceSyncService.
/// </summary>
/// <remarks>
/// Parameter type of <c>IGoogleGroupSync.ReconcileOneAsync</c>, which
/// <c>HangfireGoogleGroupSyncScheduler</c> enqueues, and Hangfire serializes parameter types
/// as assembly-qualified names: moving or renaming this enum makes any job already queued at
/// deploy fail visibly into Hangfire's Failed list. Group sync re-converges on the next
/// reconciliation, so that is accepted rather than guarded.
/// </remarks>
public enum SyncAction
{
    /// <summary>Compute diff only, make no changes.</summary>
    Preview = 0,
    /// <summary>Compute diff and execute changes (adds/removes per SyncSettings).</summary>
    Execute = 1
}
