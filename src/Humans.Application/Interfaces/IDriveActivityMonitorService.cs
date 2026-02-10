namespace Humans.Application.Interfaces;

/// <summary>
/// Service for monitoring Google Drive Activity API for anomalous permission changes
/// on managed resources (Shared Drive folders and Google Groups).
/// </summary>
public interface IDriveActivityMonitorService
{
    /// <summary>
    /// Checks Drive Activity API for permission changes not initiated by the system's
    /// service account and logs anomalous changes to the audit log.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of anomalous activities detected.</returns>
    Task<int> CheckForAnomalousActivityAsync(CancellationToken cancellationToken = default);
}
