using Humans.Base.Interfaces;
namespace Humans.Monitor.Contracts;

/// <summary>
/// Service for monitoring Google Drive Activity API for anomalous permission changes
/// on managed Drive folders.
/// </summary>
public interface IDriveActivityMonitorService : IApplicationService
{
    /// <summary>
    /// Checks Drive Activity API for permission changes not initiated by the system's
    /// service account and logs anomalous changes to the audit log.
    /// </summary>
    /// <returns>The number of anomalous activities detected.</returns>
    Task<int> CheckForAnomalousActivityAsync(CancellationToken cancellationToken = default);
}
