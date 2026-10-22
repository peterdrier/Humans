using Humans.Base.Enums;

namespace Humans.GoogleIntegration.Services;

/// <summary>Keeps the service layer off the background-job runtime.</summary>
internal interface IGoogleGroupSyncScheduler
{
    void Enqueue(string groupKey, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    void Schedule(string groupKey, TimeSpan delay, int retryAttempt, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);
}
