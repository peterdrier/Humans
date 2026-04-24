namespace Humans.Application.Interfaces;

/// <summary>
/// Cross-cutting metrics surface for background job runs. Every Hangfire job
/// records a success/failure outcome through this interface after each execution
/// (issue nobodies-collective/Humans#580).
/// </summary>
/// <remarks>
/// This does not belong to any single section — <c>humans.job_runs_total</c> is
/// emitted from every job in <c>Humans.Infrastructure/Jobs/</c>. The contributor
/// implementation lives in <c>Humans.Infrastructure</c> alongside the jobs that
/// consume it.
/// </remarks>
public interface IJobRunMetrics
{
    /// <summary>
    /// Increments <c>humans.job_runs_total</c> with <c>job</c> and
    /// <c>result</c> tags (result = "success" / "failure" / "skipped").
    /// </summary>
    void RecordJobRun(string job, string result);
}
