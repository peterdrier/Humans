namespace Humans.Application.Interfaces;

/// <summary>
/// Records Prometheus metrics for business operations.
/// </summary>
public interface IHumansMetrics
{
    void RecordEmailSent(string template);
    void RecordConsentGiven();
    void RecordMemberSuspended(string source);
    void RecordVolunteerApproved();
    void RecordSyncOperation(string result);
    void RecordApplicationProcessed(string action);
    void RecordJobRun(string job, string result);
    void RecordEmailQueued(string template);
    void RecordEmailFailed(string template);
}
