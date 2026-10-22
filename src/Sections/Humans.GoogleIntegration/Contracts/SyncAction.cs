namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Add/remove behaviour comes from the admin SyncMode. Hangfire stores this type by assembly-qualified name, so
/// moving or renaming it fails any group-sync job queued at deploy; reconciliation re-converges, so that's accepted.
/// </summary>
public enum SyncAction
{
    Preview = 0,
    Execute = 1
}
