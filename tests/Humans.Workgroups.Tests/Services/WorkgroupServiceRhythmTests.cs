using Humans.Users.Contracts;
using Microsoft.Extensions.Logging;
using AwesomeAssertions;
using NSubstitute.ExceptionExtensions;
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
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task UpdateNudge_UsesTheResolvedRecipientsLanguage_AndDeduplicatesAliases(bool includeSurvivor)
    {
        var formerId = SeedUser("Former account");
        var survivorId = SeedUser("Survivor", language: "es");
        var survivor = (await Users.GetUserInfoAsync(survivorId, Ct))!;
        var workgroup = await SeedWorkgroupAsync(coordinatorUserId: formerId,
            registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));
        if (includeSurvivor)
            await AddMemberAsync(workgroup.Id, survivorId, WorkgroupMemberRole.Coordinator);
        // Users' batched read retains requested keys while resolving their values.
        Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>()
                .ToDictionary(id => id, _ => survivor));

        await NewService().RunDailyRhythmAsync(Ct);

        await Notifications.Received(1).SendAsync(
            WorkgroupsNotificationSources.WorkgroupReportingDue, Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
            "Actualización mensual pendiente: " + workgroup.Name,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == survivorId),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task UpdateNudge_FailedLanguage_DoesNotSuppressOthersOrOverstateDelivery(bool hasAnotherLanguage)
    {
        var group = await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));
        var first = group.Members.Single().UserId;
        var second = SeedUser("Segundo", language: "es");
        if (hasAnotherLanguage)
            await AddMemberAsync(group.Id, second, WorkgroupMemberRole.Coordinator);
        Notifications.SendAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(),
                Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(first)),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("English notice storage unavailable"));

        await NewService().RunDailyRhythmAsync(Ct);

        await Notifications.Received(hasAnotherLanguage ? 1 : 0).SendAsync(WorkgroupsNotificationSources.WorkgroupReportingDue,
            Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == second),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        if (hasAnotherLanguage)
        {
            await AuditLog.Received(1).LogAsync(WorkgroupsAuditActions.WorkgroupUpdateDueNotified,
                AuditEntityTypes.Workgroup, group.Id,
                Arg.Is<string>(description => description.Contains("1 coordinator", StringComparison.Ordinal)),
                WorkgroupRhythm.WorkgroupRhythmJobName);
        }
        else
        {
            await AuditLog.DidNotReceive().LogAsync(WorkgroupsAuditActions.WorkgroupUpdateDueNotified,
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
        }
    }

    [HumansTheory]
    [Xunit.InlineData("Recipients")]
    [Xunit.InlineData("CoordinatorNotice")]
    [Xunit.InlineData("BoardNotice")]
    public async Task JobCancellation_DuringTheLastGroup_PropagatesWithoutAnErrorLog(string boundary)
    {
        using var shutdown = new CancellationTokenSource();
        var boardNotice = string.Equals(boundary, "BoardNotice", StringComparison.Ordinal);
        var now = Clock.GetCurrentInstant();
        var workgroup = await SeedWorkgroupAsync(
            status: boardNotice ? WorkgroupStatus.Applied : WorkgroupStatus.Active,
            appliedAt: now.Minus(Duration.FromDays(14)),
            registeredAt: now.Minus(Duration.FromDays(30)));
        async Task CancelAsync(CancellationToken ct)
        {
            await shutdown.CancelAsync();
            ct.Should().Be(shutdown.Token);
            ct.ThrowIfCancellationRequested();
        }
        async Task<IReadOnlyDictionary<Guid, UserInfo>> CancelLookupAsync(CancellationToken ct)
        {
            await CancelAsync(ct);
            return new Dictionary<Guid, UserInfo>();
        }
        if (string.Equals(boundary, "Recipients", StringComparison.Ordinal))
        {
            Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(call => new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(CancelLookupAsync(call.Arg<CancellationToken>())));
        }
        else if (boardNotice)
        {
            Notifications.SendToRoleAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call => CancelAsync(call.Arg<CancellationToken>()));
        }
        else
        {
            Notifications.SendAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call => CancelAsync(call.Arg<CancellationToken>()));
        }

        var act = () => NewService().RunDailyRhythmAsync(shutdown.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(shutdown.Token);
        Logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        var warning = Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;
        warning.Exception.Should().BeNull();
        warning.Message.Should().Contain(workgroup.Id.ToString());
        await AuditLog.DidNotReceive().LogAsync(WorkgroupsAuditActions.WorkgroupUpdateDueNotified,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task ThirtyDaysSilent_NudgesCoordinators_AndAudits()
    {
        var workgroup = await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(30)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.Received(1).LogAsync(
            WorkgroupsAuditActions.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupRhythm.WorkgroupRhythmJobName);
        await Notifications.Received(1).SendAsync(
            WorkgroupsNotificationSources.WorkgroupReportingDue, Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
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
            WorkgroupsAuditActions.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupRhythm.WorkgroupRhythmJobName);
    }

    [HumansFact]
    public async Task TwentyNineDaysSilent_DoesNotNudge()
    {
        await SeedWorkgroupAsync(registeredAt: Clock.GetCurrentInstant().Minus(Duration.FromDays(29)));

        await NewService().RunDailyRhythmAsync(Ct);

        await AuditLog.DidNotReceive().LogAsync(
            WorkgroupsAuditActions.WorkgroupUpdateDueNotified, Arg.Any<string>(), Arg.Any<Guid>(),
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
            WorkgroupsAuditActions.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupRhythm.WorkgroupRhythmJobName);

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
            WorkgroupsAuditActions.WorkgroupApplicationOverdue, AuditEntityTypes.Workgroup, workgroup.Id,
            Arg.Any<string>(), WorkgroupRhythm.WorkgroupRhythmJobName);

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
            WorkgroupsAuditActions.WorkgroupApplicationOverdue, Arg.Any<string>(), Arg.Any<Guid>(),
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
                Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), failing.Id, Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("the audit log is down for this group"));

        var act = () => NewService().RunDailyRhythmAsync(Ct);

        await act.Should().NotThrowAsync();
        await AuditLog.Received(1).LogAsync(
            WorkgroupsAuditActions.WorkgroupUpdateDueNotified, AuditEntityTypes.Workgroup, alsoQuiet.Id,
            Arg.Any<string>(), WorkgroupRhythm.WorkgroupRhythmJobName);
    }

    [HumansFact]
    public async Task AWeeklySeries_OffersItsNextOccurrence_ButScheduledWeeksAreNotActivity()
    {
        var now = Clock.GetCurrentInstant();
        var started = now.Minus(Duration.FromDays(60));
        var group = await SeedWorkgroupAsync(registeredAt: started);
        await AddMeetingAsync(group.Id, started, weeklyIn: "UTC");

        var info = (await NewService().GetByIdAsync(group.Id, Ct))!;

        info.IsUpdateDue(now).Should().BeTrue();
        info.NextMeeting(now)!.StartUtc.Should().Be(started.Plus(Duration.FromDays(63)));
    }

    [HumansFact]
    public async Task AFarOffMeeting_IsStillTheNextOne()
    {
        var now = Clock.GetCurrentInstant();
        var group = await SeedWorkgroupAsync();
        var farOff = now.Plus(Duration.FromDays(3 * 366));
        await AddMeetingAsync(group.Id, farOff);

        var info = (await NewService().GetByIdAsync(group.Id, Ct))!;

        info.NextMeeting(now)!.StartUtc.Should().Be(farOff);
        info.MeetingOccurrences(now).Should().ContainSingle();
    }

    [HumansFact]
    public async Task MeetingNotes_CountAsActivity()
    {
        var now = Clock.GetCurrentInstant();
        var group = await SeedWorkgroupAsync(registeredAt: now.Minus(Duration.FromDays(60)));
        var member = group.Members.Single().UserId;
        await NewService().AddLogEntryAsync(group.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.MeetingNotes, now.InUtc().Date, null, "Decided things"), Ct);

        var info = (await NewService().GetByIdAsync(group.Id, Ct))!;

        info.IsUpdateDue(now).Should().BeFalse();
    }
}
