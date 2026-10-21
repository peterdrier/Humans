using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Services;

/// <summary>The nightly pass sends, records, and never touches a grant.</summary>
public sealed class ReminderTests : PreapprovalsTestHarness
{
    [HumansFact]
    public void ReminderKindFor_WindowEdges()
    {
        (int DaysLeft, ReminderKind? Expected)[] cases =
            [(15, null), (14, ReminderKind.D14), (4, ReminderKind.D14), (3, ReminderKind.D3), (0, ReminderKind.D3), (-1, null)];
        foreach (var (daysLeft, expected) in cases)
            PreapprovalService.ReminderKindFor(Today, Today.PlusDays(daysLeft)).Should().Be(expected, $"{daysLeft} days left");
    }

    [HumansFact]
    public async Task Run_SendsD14Once_RecordsMarker_AuditsAsJob_LeavesGrantAlone()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(10));
        var person = SeedUser("Person", language: "fr");
        var grant = await SeedGrantAsync(program, person);
        var service = NewService();

        await service.SendDeadlineRemindersAsync(Ct);
        await service.SendDeadlineRemindersAsync(Ct);

        await Notifications.Received(1).SendAsync(NotificationSource.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.FileBySourceKey(grant.Id), Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "preapproval_deadline_reminder" && m.Culture == "fr"), Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalReminderSent, AuditEntityTypes.Grant, grant.Id,
            Arg.Is<string>(d => d.StartsWith("14-day", StringComparison.Ordinal)), PreapprovalService.ReminderJobName, program.Id, AuditEntityTypes.Program);

        await using var db = OpenContext();
        var markers = await db.RemindersSent.ToListAsync(Ct);
        markers.Should().ContainSingle().Which.Should().BeEquivalentTo(new { GrantId = grant.Id, Kind = ReminderKind.D14, ClaimBy = program.ClaimBy });
        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Open);
        stored.UpdatedAt.Should().Be(grant.UpdatedAt);
    }

    [HumansFact]
    public async Task Run_D3AfterD14_HighPriority_BothRecorded()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(10));
        var grant = await SeedGrantAsync(program);
        var service = NewService();
        await service.SendDeadlineRemindersAsync(Ct);
        SetToday(Today.PlusDays(8));

        await service.SendDeadlineRemindersAsync(Ct);

        await Notifications.Received(1).SendAsync(NotificationSource.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.High,
            Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.FileBySourceKey(grant.Id), Arg.Any<CancellationToken>());
        await using var db = OpenContext();
        (await db.RemindersSent.Select(r => r.Kind).ToListAsync(Ct)).Should().BeEquivalentTo([ReminderKind.D14, ReminderKind.D3]);
    }

    [HumansFact]
    public async Task Run_SkipsNotOpen_PastDeadline_AndFarAway()
    {
        var soon = await SeedProgramAsync(claimBy: Today.PlusDays(2));
        var past = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        var far = await SeedProgramAsync(claimBy: Today.PlusDays(60));
        await SeedGrantAsync(soon, status: GrantStatus.Claimed, expenseReportId: Guid.NewGuid());
        await SeedGrantAsync(soon, status: GrantStatus.Applied);
        await SeedGrantAsync(soon, status: GrantStatus.Revoked);
        var expired = await SeedGrantAsync(past);
        await SeedGrantAsync(far);

        await NewService().SendDeadlineRemindersAsync(Ct);

        await Notifications.DidNotReceiveWithAnyArgs().SendAsync(default, default, default, default!, default!);
        await using var db = OpenContext();
        (await db.RemindersSent.CountAsync(Ct)).Should().Be(0);
        (await ReadGrantAsync(expired.Id)).Status.Should().Be(GrantStatus.Open, "a passed deadline changes nothing");
    }

    [HumansFact]
    public async Task Run_MovedDeadline_RemindsAgainForTheNewDate()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(10));
        var grant = await SeedGrantAsync(program);
        var service = NewService();
        await service.SendDeadlineRemindersAsync(Ct);

        await using (var db = OpenContext())
        {
            (await db.Programs.FindAsync([program.Id], Ct))!.ClaimBy = Today.PlusDays(12);
            await db.SaveChangesAsync(Ct);
        }
        await service.SendDeadlineRemindersAsync(Ct);

        await Notifications.Received(2).SendAsync(Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.FileBySourceKey(grant.Id), Arg.Any<CancellationToken>());
        await using var check = OpenContext();
        (await check.RemindersSent.Select(r => r.ClaimBy).ToListAsync(Ct)).Should().BeEquivalentTo([Today.PlusDays(10), Today.PlusDays(12)]);
    }

    [HumansFact]
    public async Task Run_OneFailure_DoesNotStopTheOthers()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(5));
        var broken = SeedUser("Broken");
        var fine = SeedUser("Fine");
        await SeedGrantAsync(program, broken);
        var ok = await SeedGrantAsync(program, fine);
        Notifications.SendAsync(Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
                Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Contains(broken)), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"));
        Email.SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail.StartsWith(broken.ToString(), StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"));

        await NewService().SendDeadlineRemindersAsync(Ct);

        await using var db = OpenContext();
        var markers = await db.RemindersSent.ToListAsync(Ct);
        markers.Should().Contain(m => m.GrantId == ok.Id);
    }
}
