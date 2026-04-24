using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Cross-cutting metrics contributor for the <c>humans.job_runs_total</c>
/// counter (issue nobodies-collective/Humans#580). Lives in Infrastructure
/// alongside the Hangfire jobs that emit the metric.
/// </summary>
public sealed class JobRunMetricsContributor : IJobRunMetrics, IMetricsContributor
{
    private Counter<long> _jobRuns = null!;

    public void Initialize(IHumansMetrics metrics)
    {
        _jobRuns = metrics.CreateCounter<long>(
            "humans.job_runs_total",
            description: "Total background job runs");
    }

    public void RecordJobRun(string job, string result) =>
        _jobRuns.Add(
            1,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("result", result));
}
