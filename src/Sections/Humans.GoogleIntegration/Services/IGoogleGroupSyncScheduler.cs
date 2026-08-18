namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Queues Google Group membership reconciliation work without exposing the
/// Application layer to the background-job runtime.
/// </summary>
internal interface IGoogleGroupSyncScheduler
{
    void Enqueue(string groupKey);

    void Schedule(string groupKey, TimeSpan delay, int retryAttempt);
}
