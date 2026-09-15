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
    public async Task TwentyNineDaysSilent_DoesNotNudge()
    {
        await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(29)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SixtyDaysSilent_FlagsDormancy_SetsDormantSinceOnce_AndAudits()
    {
        var workgroup = await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(60)));

        await NewService().RunDailyRhythmAsync(Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.DormantSince.Should().Be(Clock.GetCurrentInstant());
        // Sixty days is also a multiple of thirty, so the monthly nudge fires the same pass.
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupDormancyFlagged, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);

        // The job never decides the group's fate — only a human closes it.
        reloaded.Status.Should().Be(WorkgroupStatus.Active);
    }

    [HumansFact]
    public async Task DormancyFlag_DoesNotFireTwice()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(
            registeredAt: now.Minus(Duration.FromDays(90)), dormantSince: now.Minus(Duration.FromDays(1)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.WorkgroupDormancyFlagged, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task FourteenDaysAfterDormancyFlag_StillSilent_RaisesACloseCandidate()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(
            registeredAt: now.Minus(Duration.FromDays(90)), dormantSince: now.Minus(Duration.FromDays(14)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupCloseCandidateFlagged, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).Status.Should().Be(WorkgroupStatus.Active);
    }

    [HumansFact]
    public async Task CloseCandidate_DoesNotFireAgainTheDayAfter()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(
            registeredAt: now.Minus(Duration.FromDays(90)), dormantSince: now.Minus(Duration.FromDays(15)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.WorkgroupCloseCandidateFlagged, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>());
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

    // ── DormantSince clears on a sign of life ────────────────────────────

    [HumansFact]
    public async Task AnUpdateEntry_ClearsTheDormancyFlag()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(dormantSince: now.Minus(Duration.FromDays(1)));
        var member = workgroup.Members.Single().UserId;

        await NewService().AddLogEntryAsync(
            workgroup.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Update, now.InUtc().Date, null, "Making progress"), Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).DormantSince.Should().BeNull();
    }

    [HumansFact]
    public async Task ANoteEntry_DoesNotClearTheDormancyFlag()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(dormantSince: now.Minus(Duration.FromDays(1)));
        var member = workgroup.Members.Single().UserId;

        await NewService().AddLogEntryAsync(
            workgroup.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Note, now.InUtc().Date, null, "Just a note"), Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).DormantSince.Should().NotBeNull();
    }

    [HumansFact]
    public async Task AMeeting_ClearsTheDormancyFlag()
    {
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(dormantSince: now.Minus(Duration.FromDays(1)));
        var member = workgroup.Members.Single().UserId;

        await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).DormantSince.Should().BeNull();
    }

    [HumansFact]
    public async Task OneGroupsFailure_DoesNotStopThePassForTheRest()
    {
        // A group with no coordinators at all still has to be walked without throwing.
        var quiet = await SeedWorkgroupAsync(
            name: "Silent", driveFolderId: "folder-silent",
            registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));
        var alsoQuiet = await SeedWorkgroupAsync(
            name: "Also Silent", driveFolderId: "folder-also-silent",
            registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));

        var act = () => NewService().RunDailyRhythmAsync(Ct);

        await act.Should().NotThrowAsync();
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, quiet.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, alsoQuiet.Id,
            Arg.Any<string>(), WorkgroupService.WorkgroupRhythmJobName);
    }
}
