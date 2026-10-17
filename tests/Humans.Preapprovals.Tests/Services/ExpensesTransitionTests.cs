using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Services;

/// <summary>
/// The transitions Expenses drives: claim at submit, release at withdraw, consume at approve.
/// What a report may and may not do with a grant, and that nothing automated ever moves one.
/// </summary>
public sealed class ExpensesTransitionTests : PreapprovalsTestHarness
{
    private static readonly Guid Report = Guid.NewGuid();
    private static readonly Guid OtherReport = Guid.NewGuid();

    [HumansFact]
    public async Task Claim_Open_StampsAndHolds_ResolvesFileBy_Audits()
    {
        var program = await SeedProgramAsync(perPerson: 300m);
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person);

        var stamp = await NewService().ClaimAsync(grant.Id, Report, person, Ct);

        stamp.Should().Be(new GrantStamp(300m, 62900150, "acc-62900150", program.ClaimBy));
        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Claimed);
        stored.ExpenseReportId.Should().Be(Report);
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(
            NotificationSource.PreapprovalFileBy, PreapprovalService.FileBySourceKey(grant.Id), person, Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantClaimed, AuditEntityTypes.Grant, grant.Id,
            Arg.Is<string>(d => d.Contains(Report.ToString())), person, Report, AuditEntityTypes.ExpenseReport);
    }

    [HumansFact]
    public async Task Claim_ByHoldingReport_IsIdempotent_EvenAfterDeadline()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(10));
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person, status: GrantStatus.Claimed, expenseReportId: Report);
        SetToday(Today.PlusDays(20));

        var stamp = await NewService().ClaimAsync(grant.Id, Report, person, Ct);

        stamp.MaxAmount.Should().Be(300m);
        (await ReadGrantAsync(grant.Id)).Status.Should().Be(GrantStatus.Claimed);
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantClaimed, AuditEntityTypes.Grant, grant.Id,
            Arg.Is<string>(d => d.Contains("resubmit")), person, Report, AuditEntityTypes.ExpenseReport);
    }

    [HumansTheory]
    [Xunit.InlineData((int)GrantStatus.Claimed, "another expense report")]
    [Xunit.InlineData((int)GrantStatus.Revoked, "revoked")]
    [Xunit.InlineData((int)GrantStatus.Consumed, "consumed")]
    [Xunit.InlineData((int)GrantStatus.Applied, "Applied")]
    public async Task Claim_NotOpen_Throws(int statusValue, string reason)
    {
        var status = (GrantStatus)statusValue;
        var program = await SeedProgramAsync();
        var grant = await SeedGrantAsync(program, status: status, expenseReportId: status == GrantStatus.Applied ? null : OtherReport);

        var act = () => NewService().ClaimAsync(grant.Id, Report, SeedUser(), Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{reason}*");
        (await ReadGrantAsync(grant.Id)).Status.Should().Be(status);
    }

    [HumansFact]
    public async Task Claim_AfterDeadline_Throws_AndGrantStaysOpen()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        var grant = await SeedGrantAsync(program);

        var act = () => NewService().ClaimAsync(grant.Id, Report, SeedUser(), Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*deadline*");
        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Open);
        stored.MaxAmount.Should().Be(300m);
    }

    [HumansFact]
    public async Task Claim_OnTheDeadlineDay_Allowed()
    {
        var program = await SeedProgramAsync(claimBy: Today);
        var grant = await SeedGrantAsync(program);
        var stamp = await NewService().ClaimAsync(grant.Id, Report, SeedUser(), Ct);
        stamp.ClaimBy.Should().Be(Today);
    }

    [HumansFact]
    public async Task Claim_AfterDeadlinePushedBack_ClaimableAgain()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        var grant = await SeedGrantAsync(program);
        var service = NewService();
        await FluentActions.Invoking(() => service.ClaimAsync(grant.Id, Report, SeedUser(), Ct))
            .Should().ThrowAsync<InvalidOperationException>();

        await using (var db = OpenContext())
        {
            var p = await db.Programs.FindAsync([program.Id], Ct);
            p!.ClaimBy = Today.PlusDays(30);
            await db.SaveChangesAsync(Ct);
        }

        var stamp = await service.ClaimAsync(grant.Id, Report, SeedUser(), Ct);
        stamp.ClaimBy.Should().Be(Today.PlusDays(30));
    }

    [HumansFact]
    public async Task Claim_Unknown_Throws()
    {
        var act = () => NewService().ClaimAsync(Guid.NewGuid(), Report, SeedUser(), Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task Release_ByHoldingReport_OpensAgain_KeepsReportId_Notifies_Audits()
    {
        var program = await SeedProgramAsync();
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person, status: GrantStatus.Claimed, expenseReportId: Report);

        await NewService().ReleaseAsync(grant.Id, Report, person, Ct);

        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Open);
        stored.ExpenseReportId.Should().Be(Report);
        await Notifications.Received(1).SendAsync(NotificationSource.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.FileBySourceKey(grant.Id), Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantReleased, AuditEntityTypes.Grant, grant.Id, Arg.Any<string>(), person, Report, AuditEntityTypes.ExpenseReport);
    }

    [HumansFact]
    public async Task Consume_ByHoldingReport_Terminal_Audits()
    {
        var program = await SeedProgramAsync();
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person, status: GrantStatus.Claimed, expenseReportId: Report);

        await NewService().ConsumeAsync(grant.Id, Report, person, Ct);

        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Consumed);
        stored.ExpenseReportId.Should().Be(Report);
        stored.MaxAmount.Should().Be(300m);
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantConsumed, AuditEntityTypes.Grant, grant.Id, Arg.Any<string>(), person, Report, AuditEntityTypes.ExpenseReport);
        await Notifications.DidNotReceiveWithAnyArgs().SendAsync(default, default, default, default!, default!, default, default, default, default, default, default);
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task ReleaseAndConsume_RefusedForAnotherReport_OrNotClaimed(bool release)
    {
        var program = await SeedProgramAsync();
        var held = await SeedGrantAsync(program, status: GrantStatus.Claimed, expenseReportId: OtherReport);
        var open = await SeedGrantAsync(program);
        var service = NewService();
        Func<Guid, Task> act = release
            ? id => service.ReleaseAsync(id, Report, SeedUser(), Ct)
            : id => service.ConsumeAsync(id, Report, SeedUser(), Ct);

        await FluentActions.Invoking(() => act(held.Id)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*not held by*");
        await FluentActions.Invoking(() => act(open.Id)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*not held by*");
        (await ReadGrantAsync(held.Id)).Status.Should().Be(GrantStatus.Claimed);
        (await ReadGrantAsync(open.Id)).Status.Should().Be(GrantStatus.Open);
    }

    [HumansFact]
    public async Task RevokedWhileClaimed_RefusesClaimAndConsume_WithRevokedReason()
    {
        var program = await SeedProgramAsync();
        var manager = SeedUser("Manager");
        var grant = await SeedGrantAsync(program, status: GrantStatus.Claimed, expenseReportId: Report);
        var service = NewService();
        (await service.RevokeAsync(grant.Id, "budget cut", manager, Ct)).Refusal.Should().BeNull();

        await FluentActions.Invoking(() => service.ClaimAsync(grant.Id, Report, SeedUser(), Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*revoked*");
        await FluentActions.Invoking(() => service.ConsumeAsync(grant.Id, Report, SeedUser(), Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*revoked*");
        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Revoked);
        stored.ExpenseReportId.Should().Be(Report);
        stored.MaxAmount.Should().Be(300m);
    }

    [HumansFact]
    public async Task ListClaimable_OnlyOpenBeforeDeadline_AcrossMergedIds()
    {
        var live = await SeedProgramAsync(name: "Live");
        var past = await SeedProgramAsync(name: "Past", claimBy: Today.PlusDays(-1));
        var oldId = Guid.NewGuid();
        var person = SeedUser("Person", mergedIds: [oldId]);
        var mine = await SeedGrantAsync(live, person, label: "Fire Lotus");
        var underOldId = await SeedGrantAsync(live, oldId);
        await SeedGrantAsync(live, person, status: GrantStatus.Claimed, expenseReportId: Report);
        await SeedGrantAsync(live, person, status: GrantStatus.Applied);
        await SeedGrantAsync(live, person, status: GrantStatus.Revoked);
        await SeedGrantAsync(past, person);
        await SeedGrantAsync(live);

        var options = await NewService().ListClaimableForUserAsync(person, Ct);

        options.Select(o => o.Id).Should().BeEquivalentTo([mine.Id, underOldId.Id]);
        options.Single(o => o.Id == mine.Id).Should().Be(
            new GrantOption(mine.Id, "Live", "Fire Lotus", 300m, 62900150, "acc-62900150", live.ClaimBy, GrantStatus.Open));
    }

    [HumansFact]
    public async Task GetGrant_AnyState_OrNull()
    {
        var program = await SeedProgramAsync();
        var grant = await SeedGrantAsync(program, status: GrantStatus.Consumed, expenseReportId: Report);
        var service = NewService();
        (await service.GetGrantAsync(grant.Id, Ct))!.Status.Should().Be(GrantStatus.Consumed);
        (await service.GetGrantAsync(Guid.NewGuid(), Ct)).Should().BeNull();
    }
}
