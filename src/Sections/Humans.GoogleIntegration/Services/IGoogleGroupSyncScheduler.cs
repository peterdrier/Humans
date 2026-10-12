using Humans.Base.Enums;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Queues Google Group membership reconciliation work without exposing the
/// service layer to the background-job runtime.
/// </summary>
internal interface IGoogleGroupSyncScheduler
{
    void Enqueue(string groupKey, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    void Schedule(string groupKey, TimeSpan delay, int retryAttempt, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);
}
