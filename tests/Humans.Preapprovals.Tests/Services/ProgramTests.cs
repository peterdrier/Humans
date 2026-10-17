using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Services;

/// <summary>Program create/edit: validation, the kind lock, the ceiling floor, the deadline move fan-out.</summary>
public sealed class ProgramTests : PreapprovalsTestHarness
{
    private static ProgramSave Save(
        ProgramKind kind = ProgramKind.FixedPerPerson, decimal? perPerson = 300m, decimal? ceiling = 1000m,
        int account = 62900150, LocalDate? claimBy = null, ProgramStatus status = ProgramStatus.Open,
        bool accepts = true, string name = "2026 Creativity", params Guid[] managers) =>
        new(name, "desc", kind, perPerson, ceiling, account, accepts, claimBy ?? Today.PlusMonths(3), status, managers);

    [HumansFact]
    public async Task Create_StoresProgramManagersAndAccountId_AndAudits()
    {
        var actor = SeedUser("Board");
        var manager = SeedUser("Manager");

        var result = await NewService().CreateProgramAsync(Save(managers: manager), actor, Ct);

        result.Refusal.Should().BeNull();
        await using var db = OpenContext();
        var program = await db.Programs.Include(p => p.Managers).SingleAsync(Ct);
        program.HoldedAccountId.Should().Be("acc-62900150");
        program.Managers.Should().ContainSingle(m => m.UserId == manager);
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalProgramCreated, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalProgramManagerAdded, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, manager, AuditEntityTypes.User);
    }

    [HumansTheory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-5)]
    public async Task Create_ZeroOrNegativePerPersonAmount_Refused(int amount)
    {
        var result = await NewService().CreateProgramAsync(Save(perPerson: amount), SeedUser(), Ct);
        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.AmountPositive);
    }

    [HumansFact]
    public async Task Create_FixedWithoutPerPerson_AndPerGrantWithPerPerson_Refused()
    {
        var service = NewService();
        (await service.CreateProgramAsync(Save(perPerson: null), SeedUser(), Ct)).Refusal!.Key
            .Should().Be(PreapprovalErrorKeys.PerPersonAmountRequired);
        (await service.CreateProgramAsync(Save(kind: ProgramKind.PerGrantAmount, perPerson: 10), SeedUser(), Ct)).Refusal!.Key
            .Should().Be(PreapprovalErrorKeys.PerPersonAmountNotAllowed);
    }

    [HumansFact]
    public async Task Create_UnknownAccount_Refused()
    {
        var result = await NewService().CreateProgramAsync(Save(account: 1), SeedUser(), Ct);
        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.AccountNotFound);
    }

    [HumansFact]
    public async Task Edit_KindLockedOnceGrantsExist()
    {
        var program = await SeedProgramAsync();
        await SeedGrantAsync(program);

        var result = await NewService().UpdateProgramAsync(program.Id, Save(kind: ProgramKind.PerGrantAmount, perPerson: null), SeedUser(), Ct);

        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.KindLocked);
    }

    [HumansFact]
    public async Task Edit_CeilingBelowCommitted_RefusedWithCommittedAmount()
    {
        var program = await SeedProgramAsync(ceiling: 1000m);
        await SeedGrantAsync(program, amount: 300m);
        await SeedGrantAsync(program, amount: 300m, status: GrantStatus.Consumed);
        await SeedGrantAsync(program, amount: 300m, status: GrantStatus.Revoked); // not committed

        var result = await NewService().UpdateProgramAsync(program.Id, Save(ceiling: 500m), SeedUser(), Ct);

        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.CeilingBelowCommitted);
        result.Refusal.Args[0].Should().Be("600.00 €");
    }

    [HumansFact]
    public async Task Edit_ChangesManagers_AuditsEachAddAndRemove()
    {
        var old = SeedUser("Old"); var kept = SeedUser("Kept"); var added = SeedUser("Added");
        var program = await SeedProgramAsync(managers: [old, kept]);
        var actor = SeedUser("Board");

        var result = await NewService().UpdateProgramAsync(program.Id, Save(managers: [kept, added]), actor, Ct);

        result.Refusal.Should().BeNull();
        await using var db = OpenContext();
        (await db.Managers.Where(m => m.ProgramId == program.Id).Select(m => m.UserId).ToListAsync(Ct))
            .Should().BeEquivalentTo([kept, added]);
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalProgramManagerAdded, AuditEntityTypes.Program, program.Id, Arg.Any<string>(), actor, added, AuditEntityTypes.User);
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalProgramManagerRemoved, AuditEntityTypes.Program, program.Id, Arg.Any<string>(), actor, old, AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task Edit_MaxAmountEditsAreAuditedWithOldAndNew()
    {
        var program = await SeedProgramAsync(ceiling: 1000m);
        var actor = SeedUser("Board");

        await NewService().UpdateProgramAsync(program.Id, Save(ceiling: 2000m), actor, Ct);

        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalProgramEdited, AuditEntityTypes.Program, program.Id,
            Arg.Is<string>(s => s.Contains("ceiling 1,000.00 € → 2,000.00 €")), actor, null, null);
    }

    [HumansFact]
    public async Task Edit_Close_AuditsAsClosed_LeavesGrantsUntouched()
    {
        var program = await SeedProgramAsync();
        var grant = await SeedGrantAsync(program, amount: 300m);
        var actor = SeedUser("Board");

        await NewService().UpdateProgramAsync(program.Id, Save(status: ProgramStatus.Closed), actor, Ct);

        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalProgramClosed, AuditEntityTypes.Program, program.Id, Arg.Any<string>(), actor, null, null);
        var after = await ReadGrantAsync(grant.Id);
        after.Status.Should().Be(GrantStatus.Open);
        after.MaxAmount.Should().Be(300m);
    }

    [HumansFact]
    public async Task Edit_ClaimByMoved_ResolvesOldPrompt_NotifiesAndEmailsEveryOpenHolder()
    {
        var program = await SeedProgramAsync();
        var open = await SeedGrantAsync(program, beneficiary: SeedUser("Open", language: "es"));
        var claimed = await SeedGrantAsync(program, status: GrantStatus.Claimed, expenseReportId: Guid.NewGuid());
        var newDate = Today.PlusMonths(5);
        var actor = SeedUser("Board");

        await NewService().UpdateProgramAsync(program.Id, Save(claimBy: newDate), actor, Ct);

        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(NotificationSource.PreapprovalFileBy, PreapprovalService.FileBySourceKey(open.Id), actor, Ct);
        await NotificationResolve.DidNotReceive().ResolveBySourceKeyAsync(NotificationSource.PreapprovalFileBy, PreapprovalService.FileBySourceKey(claimed.Id), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await Notifications.Received(1).SendAsync(NotificationSource.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.High,
            Arg.Is<string>(t => t.Contains("2026 Creativity")), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == open.BeneficiaryUserId),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.FileBySourceKey(open.Id), Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Is<Humans.Email.Contracts.EmailMessage>(m =>
            m.TemplateName == "preapproval_deadline_changed" && m.Culture == "es"), Arg.Any<CancellationToken>());
        (await OpenContext().Programs.SingleAsync(Ct)).ClaimBy.Should().Be(newDate);
    }

    [HumansFact]
    public async Task Edit_UnchangedAccountNumber_KeepsIdEvenWhenFinanceNoLongerListsIt()
    {
        var program = await SeedProgramAsync();
        await using (var db = OpenContext())
        {
            var p = await db.Programs.SingleAsync(Ct);
            p.HoldedAccountNumber = 99; p.HoldedAccountId = "acc-retired";
            await db.SaveChangesAsync(Ct);
        }

        var result = await NewService().UpdateProgramAsync(program.Id, Save(account: 99), SeedUser(), Ct);

        result.Refusal.Should().BeNull();
        (await OpenContext().Programs.SingleAsync(Ct)).HoldedAccountId.Should().Be("acc-retired");
    }
}
