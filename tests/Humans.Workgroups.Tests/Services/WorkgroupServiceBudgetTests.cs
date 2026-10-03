using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using Humans.Workgroups.Data;

namespace Humans.Workgroups.Tests.Services;

/// <summary>The budget allocation: amount on the register, account through Finance, never a twin.</summary>
public sealed class WorkgroupServiceBudgetTests : WorkgroupsTestHarness
{
    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetBudget_AccountResolutionSurvivesRequestCancellation(bool linkExisting)
    {
        var group = await SeedWorkgroupAsync();
        var actor = group.Members.Single().UserId;
        using var cancellation = new CancellationTokenSource();
        int? requested = linkExisting ? 62900170 : null;
        Finance.CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), requested, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await cancellation.CancelAsync();
                return new HoldedExpenseAccountRef(62900170, "account", "Workgroups", !linkExisting);
            });

        var result = await NewService().SetBudgetAsync(group.Id, actor, new WorkgroupBudgetSave(100m, requested), cancellation.Token);

        result!.Created.Should().Be(!linkExisting);
        await Finance.Received(1).CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), requested, CancellationToken.None);
        await using var db = OpenContext();
        var saved = (await db.Workgroups.FindAsync([group.Id], Ct))!;
        saved.BudgetAmount.Should().Be(100m);
        saved.HoldedAccountId.Should().Be("account");
        (await db.LogEntries.CountAsync(e => e.WorkgroupId == group.Id && e.Kind == WorkgroupLogKind.BudgetSet, Ct)).Should().Be(1);
        AuditLog.ReceivedCalls().Should().ContainSingle();
    }

    [HumansFact]
    public async Task SetBudget_ExistingBindingFinishesLogAfterRequestCancellation()
    {
        var group = await SeedWorkgroupAsync();
        await BindAsync(group.Id, 62900160, "account", 100m);
        using var cancellation = new CancellationTokenSource();
        var real = new WorkgroupRepository(DbFactory);
        var repository = Substitute.For<IWorkgroupRepository>();
        repository.GetWorkgroupAsync(group.Id, Arg.Any<CancellationToken>())
            .Returns(c => real.GetWorkgroupAsync(group.Id, c.Arg<CancellationToken>()));
        repository.UpdateWorkgroupAsync(Arg.Any<Workgroup>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                c.Arg<CancellationToken>().Should().Be(cancellation.Token);
                await real.UpdateWorkgroupAsync(c.Arg<Workgroup>(), c.Arg<CancellationToken>());
                await cancellation.CancelAsync();
            });
        repository.AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(c => real.AddLogEntryAsync(c.Arg<WorkgroupLogEntry>(), c.Arg<CancellationToken>()));

        await NewService(repository).SetBudgetAsync(group.Id, group.Members.Single().UserId, new WorkgroupBudgetSave(200m, null), cancellation.Token);

        await using var db = OpenContext();
        (await db.Workgroups.FindAsync([group.Id], Ct))!.BudgetAmount.Should().Be(200m);
        (await db.LogEntries.CountAsync(e => e.WorkgroupId == group.Id && e.Kind == WorkgroupLogKind.BudgetSet, Ct)).Should().Be(1);
        await repository.DidNotReceive().AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), cancellation.Token);
        await Finance.DidNotReceive().CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        AuditLog.ReceivedCalls().Should().ContainSingle();
    }

    [HumansFact]
    public async Task SetBudget_FirstTime_CreatesTheAccountNamedAfterTheGroup()
    {
        var workgroup = await SeedWorkgroupAsync(name: "ALM 2027");
        var secretary = SeedUser("Secretary");

        var result = await NewService().SetBudgetAsync(workgroup.Id, secretary, new WorkgroupBudgetSave(1500m, null), Ct);

        await Finance.Received(1).CreateOrLinkExpenseAccountAsync("Workgroups / ALM 2027", null, Arg.Any<CancellationToken>());
        result!.Created.Should().BeTrue();
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.Include(w => w.LogEntries).SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().Be(1500m);
        reloaded.HoldedAccountNumber.Should().Be(62900150);
        reloaded.HoldedAccountId.Should().Be("acc-62900150");
        reloaded.LogEntries.Should().ContainSingle(e => e.Kind == WorkgroupLogKind.BudgetSet);
        await AuditLog.Received(1).LogAsync(AuditAction.WorkgroupBudgetSet, Arg.Any<string>(), workgroup.Id,
            Arg.Is<string>(d => d.Contains("1500") && d.Contains("62900150")), secretary,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetBudget_AccountAlreadyBound_ChangesAmountOnly()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(250m, null), Ct);

        await Finance.DidNotReceive().CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().Be(250m);
        reloaded.HoldedAccountNumber.Should().Be(62900160);
    }

    [HumansFact]
    public async Task SetBudget_ClearingKeepsTheAccountBinding()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(null, null), Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().BeNull();
        reloaded.HoldedAccountNumber.Should().Be(62900160);
        reloaded.HoldedAccountId.Should().Be("acc-160");
    }

    [HumansFact]
    public async Task SetBudget_LinkExisting_RebindsToThatAccount()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(100m, 62900170), Ct);

        await Finance.Received(1).CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), 62900170, Arg.Any<CancellationToken>());
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.HoldedAccountNumber.Should().Be(62900170);
        reloaded.HoldedAccountId.Should().Be("acc-62900170");
    }

    [HumansFact]
    public async Task SetBudget_NegativeAmount_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();

        var act = () => NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(-1m, null), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key.Should().Be(WorkgroupErrorKeys.BudgetNegative);
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Refused))]
    [InlineData(nameof(WorkgroupStatus.Withdrawn))]
    public async Task SetBudget_OnRefusedOrWithdrawn_Throws(string statusName)
    {
        var workgroup = await SeedWorkgroupAsync(status: Enum.Parse<WorkgroupStatus>(statusName));

        var act = () => NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(10m, null), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key.Should().Be(WorkgroupErrorKeys.WrongStatus);
    }

    [HumansFact]
    public async Task SetBudget_FinanceFails_WritesNothing()
    {
        var workgroup = await SeedWorkgroupAsync();
        Finance.CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<HoldedExpenseAccountRef>(_ => throw new InvalidOperationException("Holded has no expense account 1."));

        var act = () => NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(10m, 1), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key.Should().Be(WorkgroupErrorKeys.BudgetAccountFailed);
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().BeNull();
        reloaded.HoldedAccountNumber.Should().BeNull();
    }

    [HumansFact]
    public async Task ListExpenseAccounts_HoldedFails_ReturnsEmpty()
    {
        Holded.ListExpenseAccountsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<HoldedExpenseAccountDto>>(_ => throw new HttpRequestException("Holded down"));

        (await NewService().ListExpenseAccountsAsync(Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Close_RetiresTheAccount()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().CloseAsync(workgroup.Id, SeedUser("Secretary"), "Quiet for months", Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MarkDone_RetiresTheAccount()
    {
        var coordinator = SeedUser("Coordinator");
        var workgroup = await SeedWorkgroupAsync(coordinatorUserId: coordinator);
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().MarkDoneAsync(workgroup.Id, coordinator, WorkgroupDormantReason.Delivered, Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Withdraw_RetiresTheAccount()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().WithdrawAsync(workgroup.Id, SeedUser("Secretary"), "Wrong register", Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Reactivate_RestoresTheAccount()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().ReactivateAsync(workgroup.Id, SeedUser("Secretary"), Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Close_NoAccountBound_DoesNotCallFinance()
    {
        var workgroup = await SeedWorkgroupAsync();

        await NewService().CloseAsync(workgroup.Id, SeedUser("Secretary"), "Quiet", Ct);

        await Finance.DidNotReceive().SetExpenseAccountActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Close_FinanceThrows_StillEndsTheGroup()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);
        Finance.SetExpenseAccountActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("Holded down"));

        await NewService().CloseAsync(workgroup.Id, SeedUser("Secretary"), "Quiet", Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).Status.Should().Be(WorkgroupStatus.Dormant);
        Logger.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    private async Task BindAsync(Guid workgroupId, int accountNum, string accountId, decimal amount)
    {
        var w = await Db.Workgroups.SingleAsync(x => x.Id == workgroupId, Ct);
        w.BudgetAmount = amount;
        w.HoldedAccountNumber = accountNum;
        w.HoldedAccountId = accountId;
        await Db.SaveChangesAsync(Ct);
    }
}
