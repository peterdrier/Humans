using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// One pass of the reporting rhythm of design §13: the monthly update nudge to a group's
/// coordinators, and clause 1's fourteen-day notice to the Board about an application
/// nobody has decided. It notifies and records; it never registers, closes or refuses
/// anything, and it does not track whether a group has gone quiet — ending a group is the
/// Board's decision alone, taken on the Board's own reading of the register.
/// </summary>
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

            // One group's failure is not the pass's: the rest still get their notices.
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

    /// <summary>Thirty days without an Update or a meeting: the coordinators get a nudge, in-app only.</summary>
    private async Task NudgeForUpdateAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (info.SilenceFor(now) is not { } silence || !info.IsUpdateDue(now))
            return;

        // Once per thirty-day window rather than every day the group stays quiet: the nudge
        // repeats on day 30, 60, 90 …, and the register badge carries it in between.
        var days = (int)silence.TotalDays;
        if (days % (int)WorkgroupRhythm.UpdateDueAfter.TotalDays != 0)
            return;

        var coordinators = info.CoordinatorUserIds();
        await NotifyAsync(coordinators, NotificationSource.WorkgroupReportingDue,
            "Workgroups_Todo_UpdateDue_Title", info, body: null, ct);
        await AuditJobAsync(AuditAction.WorkgroupUpdateDueNotified,
            workgroup, $"Notified the coordinators that an update is due after {days} days of silence");
    }

    /// <summary>Clause 1's fourteen days: the Board is told once; the queue keeps showing it.</summary>
    private async Task FlagOverdueRegistrationAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (!info.IsRegistrationOverdue(now)
            || !JustCrossed(now - info.AppliedAt, WorkgroupRhythm.RegistrationDueAfter))
        {
            return;
        }

        await AuditJobAsync(AuditAction.WorkgroupApplicationOverdue,
            workgroup, "Application still undecided fourteen days after it was made");
        await NotifyBoardAsync(NotificationSource.WorkgroupRegistrationPending,
            $"Application overdue: {workgroup.Name}", info,
            "Clause 1 gives the Secretary fourteen days. This application is past that.", ct);
    }

    /// <summary>
    /// True on the one daily pass that first sees <paramref name="elapsed"/> past
    /// <paramref name="threshold"/>. The queue row, not the notification, is the durable
    /// signal (§13), so a missed pass costs a nudge and nothing more.
    /// </summary>
    private static bool JustCrossed(Duration elapsed, Duration threshold) =>
        elapsed >= threshold && elapsed - threshold < Duration.FromDays(1);
}
