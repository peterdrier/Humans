using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Users.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    [HumansTheory]
    [Xunit.InlineData(false, null)]
    [Xunit.InlineData(false, UserState.Deleted)]
    [Xunit.InlineData(false, UserState.Merged)]
    [Xunit.InlineData(true, null)]
    [Xunit.InlineData(true, UserState.Deleted)]
    [Xunit.InlineData(true, UserState.Merged)]
    public async Task ProgramSave_RefusesUnavailableManagersWithoutPersistingAnything(bool update, UserState? state)
    {
        var actor = SeedUser();
        var current = SeedUser();
        var valid = SeedUser();
        var unavailable = SeedUser();
        var snapshot = Xunit.Assert.IsType<UserInfo>(await Users.GetUserInfoAsync(unavailable, Ct));
        Users.GetUserInfoAsync(unavailable, Arg.Any<CancellationToken>())
            .Returns(state is { } value ? snapshot with { State = value } : null);
        var program = update ? await SeedProgramAsync(managers: current) : null;
        var save = Save(name: "Changed", managers: [valid, unavailable]);

        var result = update
            ? await NewService().UpdateProgramAsync(program!.Id, save, actor, Ct)
            : await NewService().CreateProgramAsync(save, actor, Ct);

        result.Refusal.Should().NotBeNull();
        await using var db = OpenContext();
        var stored = await db.Programs.Include(p => p.Managers).ToListAsync(Ct);
        if (update)
        {
            var unchanged = stored.Should().ContainSingle().Which;
            unchanged.Name.Should().Be(program!.Name);
            unchanged.Managers.Select(m => m.UserId).Should().Equal(current);
        }
        else
            stored.Should().BeEmpty();
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ProgramSave_ResolvesAndDeduplicatesManagersBeforePersistingAndAuditing(bool update)
    {
        var actor = SeedUser();
        var archived = Guid.NewGuid();
        var survivor = SeedUser(mergedIds: [archived]);
        var person = await Users.GetUserInfoAsync(survivor, Ct);
        Users.GetUserInfoAsync(archived, Arg.Any<CancellationToken>()).Returns(person);
        var program = update ? await SeedProgramAsync() : null;
        var save = Save(managers: [archived, survivor]);

        var result = update
            ? await NewService().UpdateProgramAsync(program!.Id, save, actor, Ct)
            : await NewService().CreateProgramAsync(save, actor, Ct);

        result.Refusal.Should().BeNull();
        await using var db = OpenContext();
        var stored = await db.Programs.Include(p => p.Managers).SingleAsync(Ct);
        stored.Managers.Select(m => m.UserId).Should().Equal(survivor);
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramManagerAdded,
            AuditEntityTypes.Program, stored.Id, Arg.Any<string>(), actor, survivor, AuditSubjectTypes.User);
    }

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
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramCreated, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramManagerAdded, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, manager, AuditSubjectTypes.User);
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
        (await NewService().GetProgramAsync(program.Id, Ct))!.KindLocked.Should().BeFalse();
        await SeedGrantAsync(program);

        (await NewService().GetProgramAsync(program.Id, Ct))!.KindLocked.Should().BeTrue();
        var result = await NewService().UpdateProgramAsync(program.Id, Save(kind: ProgramKind.PerGrantAmount, perPerson: null), SeedUser(), Ct);

        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.KindLocked);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Edit_KindChange_AuditsOldAndNewSizingRules(bool toFixed)
    {
        var before = toFixed ? ProgramKind.PerGrantAmount : ProgramKind.FixedPerPerson;
        var after = toFixed ? ProgramKind.FixedPerPerson : ProgramKind.PerGrantAmount;
        var program = await SeedProgramAsync(kind: before, perPerson: toFixed ? null : 300m);
        var actor = SeedUser("Board");

        var result = await NewService().UpdateProgramAsync(program.Id,
            Save(kind: after, perPerson: toFixed ? 300m : null), actor, Ct);

        result.Refusal.Should().BeNull();
        await using var db = OpenContext();
        (await db.Programs.SingleAsync(p => p.Id == program.Id, Ct)).Kind.Should().Be(after);
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramEdited, AuditEntityTypes.Program, program.Id,
            Arg.Is<string>(description => description.Contains($"kind {before} → {after}")), actor);
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
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramManagerAdded, AuditEntityTypes.Program, program.Id, Arg.Any<string>(), actor, added, AuditSubjectTypes.User);
        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramManagerRemoved, AuditEntityTypes.Program, program.Id, Arg.Any<string>(), actor, old, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Edit_ManagerSaveFailure_PreservesFieldsAndManagersWithoutNoticesOrAudit()
    {
        var actor = SeedUser("Board");
        var oldManager = SeedUser("Old");
        var newManager = SeedUser("New");
        var program = await SeedProgramAsync(managers: oldManager);
        await SeedGrantAsync(program);
        var options = new DbContextOptionsBuilder<PreapprovalsDbContext>(
                (DbContextOptions<PreapprovalsDbContext>)Db.GetService<IDbContextOptions>())
            .AddInterceptors(new FailManagerSave())
            .Options;
        var service = new PreapprovalService(
            new PreapprovalRepository(new TestDbContextFactory<PreapprovalsDbContext>(options)),
            Users, Finance, Notifications, NotificationResolve,
            Email, EmailFactory, AuditLog, Clock, Logger);

        Func<Task> update = () => service.UpdateProgramAsync(program.Id,
            Save(name: "Renamed", claimBy: program.ClaimBy.PlusDays(10), managers: newManager), actor, Ct);

        await update.Should().ThrowAsync<IOException>();
        await using var db = OpenContext();
        var saved = await db.Programs.Include(p => p.Managers).SingleAsync(Ct);
        saved.Name.Should().Be(program.Name);
        saved.ClaimBy.Should().Be(program.ClaimBy);
        saved.Managers.Select(m => m.UserId).Should().Equal(oldManager);
        AuditLog.ReceivedCalls().Should().BeEmpty();
        Notifications.ReceivedCalls().Should().BeEmpty();
        NotificationResolve.ReceivedCalls().Should().BeEmpty();
        Email.ReceivedCalls().Should().BeEmpty();
    }

    private sealed class FailManagerSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ProgramManager>()
                .Any(e => e.State is EntityState.Added or EntityState.Deleted))
                throw new IOException("Manager persistence failed.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [HumansFact]
    public async Task Edit_MaxAmountEditsAreAuditedWithOldAndNew()
    {
        var program = await SeedProgramAsync(ceiling: 1000m);
        var actor = SeedUser("Board");

        await NewService().UpdateProgramAsync(program.Id, Save(ceiling: 2000m), actor, Ct);

        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramEdited, AuditEntityTypes.Program, program.Id,
            Arg.Is<string>(s => s.Contains("ceiling 1,000.00 € → 2,000.00 €")), actor);
    }

    [HumansFact]
    public async Task Edit_Close_AuditsAsClosed_LeavesGrantsUntouched()
    {
        var program = await SeedProgramAsync();
        var grant = await SeedGrantAsync(program, amount: 300m);
        var actor = SeedUser("Board");

        await NewService().UpdateProgramAsync(program.Id, Save(status: ProgramStatus.Closed), actor, Ct);

        await AuditLog.Received(1).LogAsync(PreapprovalsAuditActions.PreapprovalProgramClosed, AuditEntityTypes.Program, program.Id, Arg.Any<string>(), actor);
        var after = await ReadGrantAsync(grant.Id);
        after.Status.Should().Be(GrantStatus.Open);
        after.MaxAmount.Should().Be(300m);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Edit_ClaimByMoved_ResolvesOldPrompt_NotifiesAndEmailsEveryOpenHolder(bool cancelAfterSave)
    {
        var program = await SeedProgramAsync();
        var open = await SeedGrantAsync(program, beneficiary: SeedUser("Open", language: "es"));
        var claimed = await SeedGrantAsync(program, status: GrantStatus.Claimed, expenseReportId: Guid.NewGuid());
        var newDate = Today.PlusMonths(5);
        var actor = SeedUser("Board");

        using var request = new CancellationTokenSource();
        if (cancelAfterSave)
            AuditLog.LogAsync(PreapprovalsAuditActions.PreapprovalProgramEdited,
                    AuditEntityTypes.Program, program.Id, Arg.Any<string>(), actor)
                .Returns(async _ => await request.CancelAsync());
        Email.SendAsync(Arg.Any<Humans.Email.Contracts.EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await NewService().UpdateProgramAsync(program.Id, Save(claimBy: newDate), actor, request.Token);

        request.IsCancellationRequested.Should().Be(cancelAfterSave);
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(PreapprovalsNotificationSources.PreapprovalFileBy, PreapprovalNotices.FileBySourceKey(open.Id), actor, CancellationToken.None);
        await NotificationResolve.DidNotReceive().ResolveBySourceKeyAsync(PreapprovalsNotificationSources.PreapprovalFileBy, PreapprovalNotices.FileBySourceKey(claimed.Id), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await Notifications.Received(1).SendAsync(PreapprovalsNotificationSources.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.High,
            Arg.Is<string>(t => t.Contains("2026 Creativity")), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == open.BeneficiaryUserId),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalNotices.FileBySourceKey(open.Id), Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Is<Humans.Email.Contracts.EmailMessage>(m =>
            m.TemplateName == "preapproval_deadline_changed" && m.Culture == "es"), CancellationToken.None);
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
