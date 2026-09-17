using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// The daily reporting-rhythm pass of design §13 and §20: each row fires on its own
/// condition and clears correctly, the job never touches <see cref="WorkgroupStatus"/>,
/// and every action it takes is audited under the job's own name.
/// </summary>
public sealed class WorkgroupServiceRhythmTests : WorkgroupsTestHarness
{
    [HumansFact]
    public async Task ThirtyDaysSilent_NudgesCoordinators_AndAudits()
    {
        var workgroup = await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);
        await Notifications.Received(1).SendAsync(
            NotificationSource.WorkgroupReportingDue, Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(workgroup.Members.Single().UserId)),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task FutureMeeting_DoesNotPostponeTheUpdateNudge()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(registeredAt: now.Minus(Duration.FromDays(90)));
        await AddMeetingAsync(workgroup.Id, now.Plus(Duration.FromDays(90)));

        await NewService().RunDailyRhythmAsync(Ct);

        // Only a meeting that has actually started counts as a sign of life: a group cannot
        // put the nudge off by scheduling something three months out.
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);
    }

    [HumansFact]
    public async Task TwentyNineDaysSilent_DoesNotNudge()
    {
        await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(29)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SixtyDaysSilent_NudgesAndNothingElse()
    {
        var workgroup = await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(60)));

        await NewService().RunDailyRhythmAsync(Ct);

        // Sixty days is a multiple of thirty, so the monthly nudge fires — and that is the
        // whole of it. Going quiet is not the section's to notice; ending a group is the
        // Board's decision, taken on the Board's own reading of the register.
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Active);
        (await ctx.LogEntries.AnyAsync(e => e.WorkgroupId == workgroup.Id, Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task ApplicationFourteenDaysOld_IsFlaggedOverdue()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Applied, appliedAt: now.Minus(Duration.FromDays(14)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupApplicationOverdue, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).Status.Should().Be(WorkgroupStatus.Applied);
    }

    [HumansFact]
    public async Task ApplicationThirteenDaysOld_IsNotFlagged()
    {
        var now = Clock.GetCurrentInstant();
        await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, appliedAt: now.Minus(Duration.FromDays(13)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.WorkgroupApplicationOverdue, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task OneGroupsFailure_DoesNotStopThePassForTheRest()
    {
        var failing = await SeedWorkgroupAsync(
            name: "Silent", driveFolderId: "folder-silent",
            registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));
        var alsoQuiet = await SeedWorkgroupAsync(
            name: "Also Silent", driveFolderId: "folder-also-silent",
            registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));

        // The first group's turn throws mid-action; the pass has to swallow it and carry on.
        AuditLog.When(x => x.LogAsync(
                Arg.Any<AuditAction>(), Arg.Any<string>(), failing.Id, Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("the audit log is down for this group"));

        var act = () => NewService().RunDailyRhythmAsync(Ct);

        await act.Should().NotThrowAsync();
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, alsoQuiet.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);
    }
}
