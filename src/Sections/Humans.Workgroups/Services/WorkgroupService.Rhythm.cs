using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

internal sealed partial class WorkgroupService
{
    public async Task RunDailyRhythmAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var graph = await repository.GetGraphAsync(ct);

        foreach (var workgroup in graph.Workgroups)
        {
            ct.ThrowIfCancellationRequested();
            var info = ToInfo(workgroup);

            // One group's failure isn't the pass's: the rest still get their notices.
            try
            {
                if (info.Status == WorkgroupStatus.Active)
                {
                    await NudgeForUpdateAsync(workgroup, info, now, ct);
                }
                else if (info.Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred)
                {
                    await FlagOverdueRegistrationAsync(workgroup, info, now, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The workgroup rhythm pass failed for {WorkgroupId}", workgroup.Id);
            }
        }
    }

    /// <summary>In-app only.</summary>
    private async Task NudgeForUpdateAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (info.SilenceFor(now) is not { } silence || !info.IsUpdateDue(now))
            return;

        // Once per thirty-day window (day 30, 60, 90 …); the register badge carries it in between.
        var days = (int)silence.TotalDays;
        if (days % (int)WorkgroupRhythm.UpdateDueAfter.TotalDays != 0)
            return;

        var coordinators = info.CoordinatorUserIds();
        var dispatched = await NotifyAsync(coordinators, WorkgroupsNotificationSources.WorkgroupReportingDue,
            "Workgroups_Todo_UpdateDue_Title", info, body: null, ct);
        if (dispatched > 0)
            await AuditJobAsync(WorkgroupsAuditActions.WorkgroupUpdateDueNotified,
                workgroup, $"Requested update notices for {dispatched} coordinators after {days} days of silence");
    }

    /// <summary>The Board is told once; the queue keeps showing it.</summary>
    private async Task FlagOverdueRegistrationAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (!info.IsRegistrationOverdue(now)
            || !JustCrossed(now - info.AppliedAt, WorkgroupRhythm.RegistrationDueAfter))
        {
            return;
        }

        await AuditJobAsync(WorkgroupsAuditActions.WorkgroupApplicationOverdue,
            workgroup, "Application still undecided fourteen days after it was made");
        await NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
            $"Application overdue: {workgroup.Name}", info,
            "Clause 1 gives the Secretary fourteen days. This application is past that.", ct);
    }

    /// <summary>
    /// True on the one daily pass that first sees <paramref name="elapsed"/> past <paramref name="threshold"/>.
    /// The queue row is the durable signal, so a missed pass costs only a nudge.
    /// </summary>
    private static bool JustCrossed(Duration elapsed, Duration threshold) =>
        elapsed >= threshold && elapsed - threshold < Duration.FromDays(1);
}
