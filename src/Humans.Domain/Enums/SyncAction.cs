namespace Humans.Domain.Enums;

/// <summary>
/// Whether a sync operation should preview changes or execute them.
/// Add/remove behavior is controlled by SyncSettings (Admin/SyncSettings),
/// enforced by the gateway methods in GoogleWorkspaceSyncService.
/// </summary>
public enum SyncAction
{
    /// <summary>Compute diff only, make no changes.</summary>
    Preview = 0,
    /// <summary>Compute diff and execute changes (adds/removes per SyncSettings).</summary>
    Execute = 1
}
