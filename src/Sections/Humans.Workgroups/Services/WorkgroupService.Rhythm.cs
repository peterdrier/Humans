using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// One pass of the reporting rhythm of design §13. It notifies, flags and records; it
/// never registers, closes or refuses anything — a human does that. The two remaining
/// rows of the §13 table (status overdue, disposition overdue) are read-time badges
/// computed by <see cref="WorkgroupRhythm"/>, not job actions, so they are not here.
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
                    await FlagDormancyAsync(workgroup, info, now, ct);
                    await FlagCloseCandidateAsync(workgroup, info, now, ct);
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

    /// <summary>
    /// Sixty days silent: the group is flagged, asked whether it is still going, and the
    /// Board is told. <c>DormantSince</c> is the flag itself, so this fires once — any
    /// Update or meeting clears it and the clock starts again.
    /// </summary>
    private async Task FlagDormancyAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (!info.NeedsDormancyInquiry(now))
            return;

        var days = (int)(info.SilenceFor(now)?.TotalDays ?? 0);

        // DormantSince is both the flag and the once-only latch, so it is written last: a
        // failure in the log entry, the audit record or the notices leaves the flag unset and
        // the next nightly run retries the whole step. Persisting it first would make
        // NeedsDormancyInquiry false forever with the audit trail missing.
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.DormancyInquiry, now,
            $"No update or meeting for {days} days.", ct);
        await AuditJobAsync(AuditAction.WorkgroupDormancyFlagged,
            workgroup, $"Flagged as dormant after {days} days of silence");

        var coordinators = info.CoordinatorUserIds();
        await NotifyAsync(coordinators, NotificationSource.WorkgroupReportingDue,
            "Enum_WorkgroupLogKind_DormancyInquiry", info, body: null, ct);
        await EmailAsync(coordinators, WorkgroupNoticeKind.DormancyInquiry, info, days.ToString(CultureInfo.InvariantCulture), ct);
        await NotifyBoardAsync(NotificationSource.WorkgroupReportingDue,
            $"Dormancy flagged: {workgroup.Name}", info,
            $"No update or meeting for {days} days; the coordinators have been asked.", ct);

        workgroup.DormantSince = now;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
    }

    /// <summary>
    /// Fourteen days after the flag with still nothing: the Board is asked to close it. No
    /// automatic close — the Secretary does that with written reasons.
    /// </summary>
    private async Task FlagCloseCandidateAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (!info.IsCloseCandidate(now) || !JustCrossed(now - info.DormantSince!.Value,
                WorkgroupRhythm.CloseCandidateAfter))
        {
            return;
        }

        await AuditJobAsync(AuditAction.WorkgroupCloseCandidateFlagged,
            workgroup, "Raised as a close candidate: still silent 14 days after the dormancy inquiry");
        await NotifyBoardAsync(NotificationSource.WorkgroupReportingDue,
            $"Close candidate: {workgroup.Name}", info,
            "The group has not answered the dormancy inquiry. Close it with reasons, or leave it running.", ct);
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
