using Humans.Base.Interfaces;
using Humans.Workgroups.Services;

namespace Humans.Workgroups.Jobs;

internal sealed class WorkgroupRhythmJob(IWorkgroupService workgroups) : IRecurringJob
{
    /// <summary>Hangfire's stored job id: renaming it orphans the schedule.</summary>
    public const string JobId = "workgroups-rhythm";

    public Task ExecuteAsync(CancellationToken ct = default) => workgroups.RunDailyRhythmAsync(ct);
}
