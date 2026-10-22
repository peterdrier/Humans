using Humans.Base.Interfaces;
namespace Humans.Monitor.Contracts;

public interface IDriveActivityMonitorService : IApplicationService
{
    /// <summary>
    /// Audits each permission change the service account did not make and returns the count. Throws when every
    /// resource query fails, so the job records a failed run.
    /// </summary>
    Task<int> CheckForAnomalousActivityAsync(CancellationToken cancellationToken = default);
}
