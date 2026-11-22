using System.Transactions;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
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

    [HumansTheory]
    [Xunit.InlineData("claim", true)]
    [Xunit.InlineData("claim", false)]
    [Xunit.InlineData("restamp", true)]
    [Xunit.InlineData("restamp", false)]
    [Xunit.InlineData("release", true)]
    [Xunit.InlineData("release", false)]
    [Xunit.InlineData("consume", true)]
    [Xunit.InlineData("consume", false)]
    public async Task ExpenseTransition_EmitsCompletionOnlyAfterTheReportCommits(string transition, bool commits)
    {
        var program = await SeedProgramAsync();
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person,
            status: string.Equals(transition, "claim", StringComparison.Ordinal) ? GrantStatus.Open : GrantStatus.Claimed,
            expenseReportId: string.Equals(transition, "claim", StringComparison.Ordinal) ? null : Report);
        var service = NewService();
        NotificationResolve.ResolveBySourceKeyAsync(PreapprovalsNotificationSources.PreapprovalFileBy,
                PreapprovalNotices.FileBySourceKey(grant.Id), person, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Inbox resolution unavailable")));
        var afterCommit = new List<Func<CancellationToken, Task>>();
        using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            var result = transition switch
            {
                "claim" or "restamp" => await service.ClaimAsync(grant.Id, Report, person, Ct, afterCommit.Add),
                "release" => await service.ReleaseAsync(grant.Id, Report, person, Ct, afterCommit.Add),
                _ => await service.ConsumeAsync(grant.Id, Report, person, Ct, afterCommit.Add)
            };
            result.Refusal.Should().BeNull();
            AuditLog.ReceivedCalls().Should().BeEmpty();
            Notifications.ReceivedCalls().Should().BeEmpty();
            NotificationResolve.ReceivedCalls().Should().BeEmpty();
            if (commits) scope.Complete();
        }
        if (commits)
            foreach (var complete in afterCommit) await complete(CancellationToken.None);

        AuditLog.ReceivedCalls().Should().HaveCount(commits ? 1 : 0);
        Notifications.ReceivedCalls().Should().HaveCount(commits && string.Equals(transition, "release", StringComparison.Ordinal) ? 1 : 0);
        NotificationResolve.ReceivedCalls().Should().HaveCount(commits && string.Equals(transition, "claim", StringComparison.Ordinal) ? 1 : 0);
    }

    [HumansFact]
    public async Task GetGrants_CarriesProgramManagers_UnderTheIdsTheyWereNamedWith()
    {
        var archivedId = Guid.NewGuid();
        var mine = await SeedProgramAsync(status: ProgramStatus.Closed, managers: archivedId);
        var other = await SeedProgramAsync(managers: SeedUser("Other manager"));
        var consumed = await SeedGrantAsync(mine, status: GrantStatus.Consumed, expenseReportId: Report);
        var elsewhere = await SeedGrantAsync(other);

        var grants = await NewService().GetGrantsAsync(Ct);

        grants.Single(g => g.Id == consumed.Id).ManagerUserIds.Should().BeEquivalentTo([archivedId]);
        grants.Single(g => g.Id == elsewhere.Id).ManagerUserIds.Should().NotContain(archivedId);
    }

    [HumansFact]
    public async Task Claim_Open_StampsAndHolds_ResolvesFileBy_Audits()
    {
        var program = await SeedProgramAsync(perPerson: 300m);
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person);

        var stamp = await CommittedAsync(c => NewService().ClaimAsync(grant.Id, Report, person, Ct, c));

        stamp.Claimed.Should().BeEquivalentTo(new { MaxAmount = 300m, HoldedAccountNumber = 62900150, HoldedAccountId = "acc-62900150", program.ClaimBy, Status = GrantStatus.Claimed });
        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Claimed);
        stored.ExpenseReportId.Should().Be(Report);
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(
            PreapprovalsNotificationSources.PreapprovalFileBy, PreapprovalNotices.FileBySourceKey(grant.Id), person, Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, AuditEntityTypes.Grant, grant.Id,
            Arg.Is<string>(d => d.Contains(Report.ToString())), person, Report, AuditEntityTypes.ExpenseReport);
    }

    [HumansFact]
    public async Task Claim_ByHoldingReport_IsIdempotent_EvenAfterDeadline()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(10));
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person, status: GrantStatus.Claimed, expenseReportId: Report);
        SetToday(Today.PlusDays(20));

        var stamp = await CommittedAsync(c => NewService().ClaimAsync(grant.Id, Report, person, Ct, c));

        stamp.Claimed!.MaxAmount.Should().Be(300m);
        (await ReadGrantAsync(grant.Id)).Status.Should().Be(GrantStatus.Claimed);
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, AuditEntityTypes.Grant, grant.Id,
            Arg.Is<string>(d => d.Contains("resubmit")), person, Report, AuditEntityTypes.ExpenseReport);
    }

    [HumansTheory]
    [Xunit.InlineData((int)GrantStatus.Claimed, "another expense report")]
    [Xunit.InlineData((int)GrantStatus.Revoked, "revoked")]
    [Xunit.InlineData((int)GrantStatus.Consumed, "consumed")]
    [Xunit.InlineData((int)GrantStatus.Applied, "Applied")]
    public async Task Claim_NotOpen_ReturnsRefusal(int statusValue, string reason)
    {
        var status = (GrantStatus)statusValue;
        var program = await SeedProgramAsync();
        var grant = await SeedGrantAsync(program, status: status, expenseReportId: status == GrantStatus.Applied ? null : OtherReport);

        var result = await CommittedAsync(c => NewService().ClaimAsync(grant.Id, Report, SeedUser(), Ct, c));

        result.Refusal.Should().Contain(reason);
        (await ReadGrantAsync(grant.Id)).Status.Should().Be(status);
    }

    [HumansFact]
    public async Task Claim_AfterDeadline_ReturnsRefusal_AndGrantStaysOpen()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        var grant = await SeedGrantAsync(program);

        var result = await CommittedAsync(c => NewService().ClaimAsync(grant.Id, Report, SeedUser(), Ct, c));

        result.Refusal.Should().Contain("deadline");
        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Open);
        stored.MaxAmount.Should().Be(300m);
    }

    [HumansFact]
    public async Task Claim_OnTheDeadlineDay_Allowed()
    {
        var program = await SeedProgramAsync(claimBy: Today);
        var grant = await SeedGrantAsync(program);
        var stamp = await CommittedAsync(c => NewService().ClaimAsync(grant.Id, Report, SeedUser(), Ct, c));
        stamp.Claimed!.ClaimBy.Should().Be(Today);
    }

    [HumansFact]
    public async Task Claim_AfterDeadlinePushedBack_ClaimableAgain()
    {
        var program = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        var grant = await SeedGrantAsync(program);
        var service = NewService();
        (await CommittedAsync(c => service.ClaimAsync(grant.Id, Report, SeedUser(), Ct, c))).Refusal.Should().Contain("deadline");

        await using (var db = OpenContext())
        {
            var p = await db.Programs.FindAsync([program.Id], Ct);
            p!.ClaimBy = Today.PlusDays(30);
            await db.SaveChangesAsync(Ct);
        }

        var stamp = await CommittedAsync(c => service.ClaimAsync(grant.Id, Report, SeedUser(), Ct, c));
        stamp.Claimed!.ClaimBy.Should().Be(Today.PlusDays(30));
    }

    [HumansFact]
    public async Task Claim_Unknown_ReturnsRefusal()
    {
        (await CommittedAsync(c => NewService().ClaimAsync(Guid.NewGuid(), Report, SeedUser(), Ct, c)))
            .Refusal.Should().Contain("does not exist");
    }

    [HumansFact]
    public async Task Release_ByHoldingReport_OpensAgain_KeepsReportId_Notifies_Audits()
    {
        var program = await SeedProgramAsync();
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person, status: GrantStatus.Claimed, expenseReportId: Report);

        (await CommittedAsync(c => NewService().ReleaseAsync(grant.Id, Report, person, Ct, c))).Refusal.Should().BeNull();

        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Open);
        stored.ExpenseReportId.Should().Be(Report);
        await Notifications.Received(1).SendAsync(PreapprovalsNotificationSources.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalNotices.FileBySourceKey(grant.Id), Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalGrantReleased, AuditEntityTypes.Grant, grant.Id, Arg.Any<string>(), person, Report, AuditEntityTypes.ExpenseReport);
    }

    [HumansFact]
    public async Task Consume_ByHoldingReport_Terminal_Audits()
    {
        var program = await SeedProgramAsync();
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, person, status: GrantStatus.Claimed, expenseReportId: Report);

        (await CommittedAsync(c => NewService().ConsumeAsync(grant.Id, Report, person, Ct, c))).Refusal.Should().BeNull();

        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Consumed);
        stored.ExpenseReportId.Should().Be(Report);
        stored.MaxAmount.Should().Be(300m);
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalGrantConsumed, AuditEntityTypes.Grant, grant.Id, Arg.Any<string>(), person, Report, AuditEntityTypes.ExpenseReport);
        await Notifications.DidNotReceiveWithAnyArgs().SendAsync(default!, default, default, default!, default!);
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
        Func<Guid, Task<PreapprovalWriteResult>> act = release
            ? id => CommittedAsync(c => service.ReleaseAsync(id, Report, SeedUser(), Ct, c))
            : id => CommittedAsync(c => service.ConsumeAsync(id, Report, SeedUser(), Ct, c));

        (await act(held.Id)).Refusal.Should().Contain("not held by");
        (await act(open.Id)).Refusal.Should().Contain("not held by");
        (await act(Guid.NewGuid())).Refusal.Should().Contain("does not exist");
        AuditLog.ReceivedCalls().Should().BeEmpty();
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
        (await service.RevokeAsync(program.Id, grant.Id, "budget cut", manager, Ct)).Refusal.Should().BeNull();

        (await CommittedAsync(c => service.ClaimAsync(grant.Id, Report, SeedUser(), Ct, c))).Refusal.Should().Contain("revoked");
        (await CommittedAsync(c => service.ConsumeAsync(grant.Id, Report, SeedUser(), Ct, c))).Refusal.Should().Contain("revoked");
        (await CommittedAsync(c => service.ReleaseAsync(grant.Id, Report, SeedUser(), Ct, c))).Refusal.Should().Contain("revoked");
        var stored = await ReadGrantAsync(grant.Id);
        stored.Status.Should().Be(GrantStatus.Revoked);
        stored.ExpenseReportId.Should().Be(Report);
        stored.MaxAmount.Should().Be(300m);
    }

    [HumansFact]
    public async Task GetGrants_EveryState_ClaimableOnlyOpenBeforeDeadline()
    {
        var live = await SeedProgramAsync(name: "Live");
        var past = await SeedProgramAsync(name: "Past", claimBy: Today.PlusDays(-1));
        var person = SeedUser("Person");
        var mine = await SeedGrantAsync(live, person, label: "Fire Lotus");
        var claimed = await SeedGrantAsync(live, person, status: GrantStatus.Claimed, expenseReportId: Report);
        var applied = await SeedGrantAsync(live, person, status: GrantStatus.Applied);
        var consumed = await SeedGrantAsync(live, person, status: GrantStatus.Consumed, expenseReportId: OtherReport);
        var lapsed = await SeedGrantAsync(past, person);

        var grants = await NewService().GetGrantsAsync(Ct);

        grants.Where(g => g.ClaimableToday).Select(g => g.Id).Should().BeEquivalentTo([mine.Id]);
        grants.Select(g => g.Id).Should().Contain([claimed.Id, applied.Id, consumed.Id, lapsed.Id]);
        grants.Single(g => g.Id == mine.Id).Should().BeEquivalentTo(new
        {
            HolderUserId = person,
            ProgramName = "Live",
            Label = "Fire Lotus",
            MaxAmount = 300m,
            HoldedAccountNumber = 62900150,
            HoldedAccountId = "acc-62900150",
            live.ClaimBy,
            Status = GrantStatus.Open,
        });
    }

    private static async Task<PreapprovalWriteResult> CommittedAsync(
        Func<Action<Func<CancellationToken, Task>>, Task<PreapprovalWriteResult>> write)
    {
        var afterCommit = new List<Func<CancellationToken, Task>>();
        var result = await write(afterCommit.Add);
        foreach (var complete in afterCommit) await complete(CancellationToken.None);
        return result;
    }
}
