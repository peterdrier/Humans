namespace Humans.Application.Interfaces;

/// <summary>
/// Records Prometheus metrics for business operations.
/// </summary>
public interface IHumansMetrics
{
    void RecordEmailSent(string template);
    void RecordSyncOperation(string result);
    void RecordJobRun(string job, string result);
    void RecordEmailQueued(string template);
    void RecordEmailFailed(string template);
}
