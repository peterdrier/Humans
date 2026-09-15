using Humans.Base.Interfaces;
using Humans.Workgroups.Services;

namespace Humans.Workgroups.Jobs;

/// <summary>
/// The daily reporting-rhythm pass of design §13. One Hangfire job, one service call: the
/// rules live in <see cref="IWorkgroupService.RunDailyRhythmAsync"/> where tests reach
/// them, and this type only says when.
/// </summary>
internal sealed class WorkgroupRhythmJob(IWorkgroupService workgroups) : IRecurringJob
{
    /// <summary>Hangfire's stored job id. Renaming it orphans the schedule, so it never changes.</summary>
    public const string JobId = "workgroups-rhythm";

    public Task ExecuteAsync(CancellationToken ct = default) => workgroups.RunDailyRhythmAsync(ct);
}
