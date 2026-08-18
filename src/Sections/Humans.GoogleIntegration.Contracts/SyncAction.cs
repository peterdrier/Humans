namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Whether a sync operation should preview changes or execute them.
/// Add/remove behavior is controlled by SyncSettings (Admin/SyncSettings),
/// enforced by the gateway methods in GoogleWorkspaceSyncService.
/// </summary>
/// <remarks>
/// Moved here from <c>Humans.Domain.Enums</c> by G5 lane 3b
/// (nobodies-collective/Humans#866), alongside its siblings <c>GoogleResourceType</c> and
/// <c>DrivePermissionLevel</c>, as part of emptying and deleting <c>Humans.Domain</c>.
///
/// An earlier attempt (lane 4b-2j, reverted on a Codex P1 against peterdrier/Humans#1310)
/// held that the move was impossible: this enum is a parameter type of
/// <c>IGoogleGroupSync.ReconcileOneAsync</c>, which <c>HangfireGoogleGroupSyncScheduler</c>
/// enqueues and schedules, and Hangfire serializes parameter types as assembly-qualified
/// names. Peter's ruling, 2026-08-15: that premise is dead and the move is approved.
/// <c>ReconcileOneAsync</c> is fire-and-forget/delayed, never a recurring job, so no
/// recurring-job id is affected. The only exposure is a job already queued or retry-delayed
/// at the moment of deploy: it fails visibly into Hangfire's Failed list rather than
/// silently, and group sync re-converges on the next reconciliation. Jobs are expected to
/// be resilient that way.
/// </remarks>
public enum SyncAction
{
    /// <summary>Compute diff only, make no changes.</summary>
    Preview = 0,
    /// <summary>Compute diff and execute changes (adds/removes per SyncSettings).</summary>
    Execute = 1
}
