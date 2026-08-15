namespace Humans.Domain.Enums;

/// <summary>
/// Whether a sync operation should preview changes or execute them.
/// Add/remove behavior is controlled by SyncSettings (Admin/SyncSettings),
/// enforced by the gateway methods in GoogleWorkspaceSyncService.
/// </summary>
/// <remarks>
/// DELIBERATELY LEFT IN BASE. This enum is a parameter type of
/// <c>IGoogleGroupSync.ReconcileOneAsync</c>, which <c>HangfireGoogleGroupSyncScheduler</c>
/// enqueues and schedules. Hangfire serializes parameter types as assembly-qualified
/// names, so moving this enum to another assembly makes every queued or retry-delayed
/// scoped group-sync job fail to resolve across the deploy. G5 lane 4b-2j moved it to
/// <c>Humans.GoogleIntegration.Contracts</c> and the move was reverted for this reason
/// (Codex P1 on peterdrier/Humans#1310). Peter's ruling, 2026-08-15: enums may stay in
/// Base. Do not move without first draining queued <c>ReconcileOneAsync</c> jobs — the
/// same constraint documented on the interface itself.
/// </remarks>
public enum SyncAction
{
    /// <summary>Compute diff only, make no changes.</summary>
    Preview = 0,
    /// <summary>Compute diff and execute changes (adds/removes per SyncSettings).</summary>
    Execute = 1
}
