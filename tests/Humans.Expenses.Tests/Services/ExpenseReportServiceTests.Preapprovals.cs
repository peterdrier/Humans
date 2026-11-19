using Humans.Users.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Xunit;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Services;
using Humans.Expenses.Tests.Infrastructure;
using Humans.Expenses.Services.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Humans.Expenses.Domain;
using Humans.Preapprovals.Contracts;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Expenses.Tests.Services;

/// <summary>
/// The pre-approval hook (docs/features/global/preapprovals.md): a report filed against a grant
/// books to the grant's account, claims it at submit, releases it on withdraw, consumes it on
/// approve, and never moves a grant on its own when the deadline passes.
/// </summary>
public sealed partial class ExpenseReportServiceTests
{
    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_SharedGrant_IsHeldThroughTransactionCompletionAcrossScopes(bool rollbackFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var grant = MakeGrant();
        var firstId = await CreateSubmittableGrantDraftAsync(owner, grant);
        var nextId = await CreateSubmittableGrantDraftAsync(owner, grant);
        Guid? committedHolder = null;
        _preapprovals.GetGrantsAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<GrantOption>)
            [grant with { HolderUserId = owner, Status = committedHolder is null ? GrantStatus.Open : GrantStatus.Claimed, ClaimableToday = committedHolder is null }]);
        _preapprovalWrites.ClaimAsync(grant.Id, Arg.Any<Guid>(), owner, ct,
            Arg.Any<Action<Func<CancellationToken, Task>>>()).Returns(call =>
        {
            var reportId = call.ArgAt<Guid>(1);
            if (committedHolder is { } held && held != reportId)
                return Task.FromResult(new PreapprovalWriteResult(Refusal: "Held by another report"));
            System.Transactions.Transaction.Current.Should().NotBeNull();
            call.ArgAt<Action<Func<CancellationToken, Task>>>(4)(token =>
            {
                System.Transactions.Transaction.Current.Should().BeNull();
                token.Should().Be(CancellationToken.None);
                committedHolder = reportId;
                return Task.CompletedTask;
            });
            return Task.FromResult(new PreapprovalWriteResult(grant));
        });
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = Substitute.For<IExpenseRepository>();
        repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _expenseRepo.GetByIdAsync(call.Arg<Guid>(), call.Arg<CancellationToken>()));
        repository.SubmitAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Instant>(),
            Arg.Any<ExpenseBooking?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var id = call.ArgAt<Guid>(0);
            if (id == firstId)
            {
                saving.SetResult();
                await release.Task.WaitAsync(ct);
                if (rollbackFirst) throw new IOException("Report save failed");
            }
            return await _expenseRepo.SubmitAsync(id, call.ArgAt<string>(1), call.ArgAt<string>(2),
                call.ArgAt<Instant>(3), call.ArgAt<ExpenseBooking?>(4), call.ArgAt<decimal?>(5), call.Arg<CancellationToken>());
        });
        ExpenseReportService ScopedService() => new(repository, _fileStorage, _budgetService, _teamService,
            _userService, _emailService, TestExpensesEmails.Create(), AuditLog, Substitute.For<IAuditViewerService>(),
            _holdedService, _finance, _financeWrite, _preapprovals, _preapprovalWrites, Clock,
            NullLogger<ExpenseReportService>.Instance, Options.Create(new TravelReimbursementConfig()));
        var first = ScopedService().SubmitWithResultAsync(firstId, owner, ct);
        await saving.Task.WaitAsync(ct);
        var next = ScopedService().SubmitWithResultAsync(nextId, owner, ct);
        release.SetResult();

        var results = await Task.WhenAll(first, next).WaitAsync(ct);

        results[0].Succeeded.Should().Be(!rollbackFirst);
        results[1].Succeeded.Should().Be(rollbackFirst);
        committedHolder.Should().Be(rollbackFirst ? nextId : firstId);
        var firstReport = (await _expenseRepo.GetByIdAsync(firstId, ct))!;
        var nextReport = (await _expenseRepo.GetByIdAsync(nextId, ct))!;
        firstReport.Status.Should().Be(rollbackFirst ? ExpenseReportStatus.Draft : ExpenseReportStatus.Submitted);
        nextReport.Status.Should().Be(rollbackFirst ? ExpenseReportStatus.Submitted : ExpenseReportStatus.Draft);
    }

    private const int GrantAccountNumber = 62900500;
    private const string GrantAccountId = "acc-grant";
    /// <summary>FakeNow is 2026-05-10 12:00 UTC: 14:00 in Madrid, so today is the 10th.</summary>
    private static readonly LocalDate GrantToday = new(2026, 5, 10);

    /// <summary>Held by nobody until <see cref="StubClaimable"/> hands it to a submitter.</summary>
    private static GrantOption MakeGrant(
        Guid? id = null, GrantStatus status = GrantStatus.Open, decimal maxAmount = 150m, LocalDate? claimBy = null)
    {
        var deadline = claimBy ?? GrantToday.PlusDays(10);
        return new(id ?? Guid.NewGuid(), Guid.Empty, [], "Burner bus", "Driver", maxAmount, GrantAccountNumber,
            GrantAccountId, deadline, status, status == GrantStatus.Open && GrantToday <= deadline);
    }

    /// <summary>What Preapprovals publishes for this grant from now on; a status other than Open is never claimable.</summary>
    private void PutGrant(GrantOption grant) =>
        _grantStore[grant.Id] = grant with { ClaimableToday = grant.ClaimableToday && grant.Status == GrantStatus.Open };

    private void StubNothingClaimable(Guid person)
    {
        foreach (var g in _grantStore.Values.Where(g => g.HolderUserId == person).ToList())
            _grantStore[g.Id] = g with { ClaimableToday = false };
    }

    private void ManageGrant(Guid manager, GrantOption grant)
    {
        var current = _grantStore.GetValueOrDefault(grant.Id, grant);
        _grantStore[grant.Id] = current with { ManagerUserIds = [.. current.ManagerUserIds, manager] };
    }

    private List<bool> TrackPreapprovalCompletions()
    {
        var completed = new List<bool>();
        void Collect(NSubstitute.Core.CallInfo call)
        {
            if (System.Transactions.Transaction.Current is not { } current) return;
            var transaction = current.Clone();
            call.ArgAt<Action<Func<CancellationToken, Task>>>(4)(token =>
            {
                using (transaction)
                    completed.Add(System.Transactions.Transaction.Current is null
                        && transaction.TransactionInformation.Status == System.Transactions.TransactionStatus.Committed
                        && !token.CanBeCanceled);
                return Task.CompletedTask;
            });
        }
        _preapprovalWrites.WhenForAnyArgs(writer => writer.ClaimAsync(default, default, default, default, default!)).Do(Collect);
        _preapprovalWrites.WhenForAnyArgs(writer => writer.ReleaseAsync(default, default, default, default, default!)).Do(Collect);
        _preapprovalWrites.WhenForAnyArgs(writer => writer.ConsumeAsync(default, default, default, default, default!)).Do(Collect);
        return completed;
    }

    /// <summary>The grant is the submitter's to file against today.</summary>
    private void StubClaimable(Guid submitter, params GrantOption[] grants)
    {
        foreach (var g in grants)
            _grantStore[g.Id] = g with { HolderUserId = submitter, ClaimableToday = true };
    }

    private async Task<Guid> CreateGrantDraftAsync(Guid submitter, GrantOption grant)
    {
        StubClaimable(submitter, grant);
        var result = await _sut.CreateDraftAsync(submitter, submitter, 0, "Project X", grant.Id, TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        return result.ReportId!.Value;
    }

    /// <summary>A submittable draft on the grant: one receipt line with its file, a payee profile.</summary>
    private async Task<Guid> CreateSubmittableGrantDraftAsync(Guid submitter, GrantOption grant)
    {
        var id = await CreateGrantDraftAsync(submitter, grant);
        var lineId = (await _sut.AddLineWithResultAsync(id, submitter, "Fuel", 120m, ct: TestContext.Current.CancellationToken)).LineId!.Value;
        await _expenseRepo.ReplaceLineAttachmentAsync(id, lineId, MakeAttachment(submitter), TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");
        return id;
    }

    private async Task SeedGrantReportAsync(Guid reportId, Guid submitter, Guid grantId, ExpenseReportStatus status, decimal? maxAmount = null)
    {
        var now = Instant.FromUtc(2026, 5, 1, 0, 0);
        await using var ctx = new ExpensesDbContext(_expensesOptions);
        ctx.ExpenseReports.Add(new ExpenseReport
        {
            Id = reportId,
            SubmitterUserId = submitter,
            HoldedAccountNumber = GrantAccountNumber,
            HoldedAccountId = GrantAccountId,
            PreapprovalGrantId = grantId,
            Note = "Project X",
            Status = status,
            MaxAmount = maxAmount,
            Total = 120m,
            CreatedAt = now,
            UpdatedAt = now
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task CreateDraft_WithGrant_BooksToTheGrantsAccount_WithNoCategory()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();

        var id = await CreateGrantDraftAsync(submitter, grant);

        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.PreapprovalGrantId.Should().Be(grant.Id);
        loaded.HoldedAccountNumber.Should().Be(GrantAccountNumber);
        loaded.HoldedAccountId.Should().Be(GrantAccountId);
        loaded.BudgetCategoryId.Should().BeNull("a pre-approved report is first reviewed by its program's managers, not a budget coordinator");
        loaded.BudgetYearId.Should().BeNull();
        loaded.MaxAmount.Should().BeNull("the cap is stamped at submit, not at draft");
        await _finance.DidNotReceive().ListExpenseAccountsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateDraft_WithGrant_RefusesOneTheSubmitterCannotClaim()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var someoneElsesGrant = MakeGrant();
        StubNothingClaimable(submitter);
        PutGrant(someoneElsesGrant);

        var result = await _sut.CreateDraftAsync(submitter, submitter, CategoryAccountNumber, "Project X", someoneElsesGrant.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalNotClaimable");
        (await _sut.GetForSubmitterAsync(submitter, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateDraft_OnBehalf_UsesTheMembersGrants_NotTheAdmins()
    {
        SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var adminsGrant = MakeGrant();
        StubClaimable(admin, adminsGrant);
        StubNothingClaimable(member);

        var result = await _sut.CreateDraftAsync(member, admin, 0, "Project X", adminsGrant.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalNotClaimable");
    }

    [HumansFact]
    public async Task UpdateDraft_KeepsTheHeldGrant_EvenWhenNoLongerClaimable()
    {
        // A rejected report corrected after the deadline keeps the grant it filed in time.
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = await CreateGrantDraftAsync(submitter, grant);
        StubNothingClaimable(submitter);
        PutGrant(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, 0, "fixed", grant.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.PreapprovalGrantId.Should().Be(grant.Id);
        loaded.Note.Should().Be("fixed");
    }

    [HumansFact]
    public async Task UpdateDraft_DroppingTheGrant_BooksFromTheAccountAgain()
    {
        var (year, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await CreateGrantDraftAsync(submitter, MakeGrant());

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, CategoryAccountNumber, null, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.PreapprovalGrantId.Should().BeNull();
        loaded.HoldedAccountNumber.Should().Be(CategoryAccountNumber);
        loaded.BudgetCategoryId.Should().Be(category.Id);
        loaded.BudgetYearId.Should().Be(year.Id);
    }

    [HumansTheory]
    [InlineData(ExpenseReportStatus.Draft, 62900600, "acc-new-program")]
    [InlineData(ExpenseReportStatus.Submitted, GrantAccountNumber, GrantAccountId)]
    [InlineData(ExpenseReportStatus.CoordinatorEndorsed, GrantAccountNumber, GrantAccountId)]
    public async Task UpdateDraft_ProgramAccountChange_DoesNotMoveASubmittedStamp(
        ExpenseReportStatus status, int expectedAccountNumber, string expectedAccountId)
    {
        var submitter = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var held = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, held.Id, status, maxAmount: 150m);
        PutGrant(held with { HoldedAccountNumber = 62900600, HoldedAccountId = "acc-new-program" });

        var result = await _sut.UpdateHeaderForFinanceCorrectionWithResultAsync(
            id, admin, GrantAccountNumber, "Corrected subject", held.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.HoldedAccountNumber.Should().Be(expectedAccountNumber);
        loaded.HoldedAccountId.Should().Be(expectedAccountId);
        loaded.MaxAmount.Should().Be(150m);
        loaded.BudgetCategoryId.Should().BeNull();
        loaded.BudgetYearId.Should().BeNull();
        loaded.Note.Should().Be("Corrected subject");
    }

    [HumansFact]
    public async Task UpdateDraft_PastDraft_RefusesChangingTheGrantOrAccount()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var held = MakeGrant();
        var other = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, held.Id, ExpenseReportStatus.Submitted);
        StubClaimable(submitter, held, other);

        var swapped = await _sut.UpdateHeaderForFinanceCorrectionWithResultAsync(id, admin, GrantAccountNumber, null, other.Id, TestContext.Current.CancellationToken);
        var rebooked = await _sut.UpdateHeaderForFinanceCorrectionWithResultAsync(id, admin, OtherAccountNumber, null, held.Id, TestContext.Current.CancellationToken);
        var noteOnly = await _sut.UpdateHeaderForFinanceCorrectionWithResultAsync(id, admin, GrantAccountNumber, "note", held.Id, TestContext.Current.CancellationToken);

        swapped.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalLocked");
        rebooked.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalLocked");
        noteOnly.Succeeded.Should().BeTrue(noteOnly.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.PreapprovalGrantId.Should().Be(held.Id);
        loaded.HoldedAccountNumber.Should().Be(GrantAccountNumber);
        loaded.Note.Should().Be("note");
    }

    [HumansFact]
    public async Task Submit_WithGrant_ClaimsIt_AndRestampsAccountAndCapFromTheGrant()
    {
        var completed = TrackPreapprovalCompletions();
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(maxAmount: 150m);
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        // The program changed since the draft: what the grant says now is what the report carries.
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>())
            .Returns(new PreapprovalWriteResult(grant with { MaxAmount = 130m, HoldedAccountNumber = 62900510, HoldedAccountId = "acc-grant-2" }));

        var result = await _sut.SubmitWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
        loaded.HoldedAccountNumber.Should().Be(62900510);
        loaded.HoldedAccountId.Should().Be("acc-grant-2");
        loaded.MaxAmount.Should().Be(130m);
        loaded.Payable.Should().Be(120m, "receipts under the cap pay in full");
        loaded.BudgetCategoryId.Should().BeNull();
        completed.Should().Equal(true);
    }

    [HumansFact]
    public async Task CoordinatorQueue_ExcludesPreapprovedReportsOutsideTheViewersPrograms()
    {
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var reportId = Guid.NewGuid();
        await SeedGrantReportAsync(reportId, submitter, grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        var otherManager = Guid.NewGuid();
        _userService.GetUserInfoAsync(otherManager, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = otherManager }, [], [], null));
        ManageGrant(otherManager, MakeGrant());

        (await _sut.GetAsync(reportId, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
        (await _sut.GetCoordinatorQueueAsync(otherManager, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Endorse_PreapprovedReport_ByItsProgramsManager_KeepsTheGrantAsTheCeiling()
    {
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(maxAmount: 150m);
        PutGrant(grant);
        var reportId = Guid.NewGuid();
        await SeedGrantReportAsync(reportId, submitter, grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        var manager = Guid.NewGuid();
        _userService.GetUserInfoAsync(manager, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = manager }, [], [], null));
        ManageGrant(manager, grant);

        (await _sut.GetCoordinatorQueueAsync(manager, TestContext.Current.CancellationToken))
            .Should().ContainSingle(r => r.Id == reportId);

        var over = await _sut.CoordinatorEndorseWithResultAsync(reportId, manager, 151m, TestContext.Current.CancellationToken);
        over.Error!.OperatorMessage.Should().Contain("cannot exceed the pre-approved 150.00");

        (await _sut.CoordinatorEndorseWithResultAsync(reportId, manager, null, TestContext.Current.CancellationToken))
            .Succeeded.Should().BeTrue();
        var endorsed = await _sut.GetAsync(reportId, TestContext.Current.CancellationToken);
        endorsed!.Status.Should().Be(ExpenseReportStatus.CoordinatorEndorsed);
        endorsed.MaxAmount.Should().Be(150m, "a blank cap keeps the pre-approved amount");
        await AuditLog.Received(1).LogAsync(ExpensesAuditActions.ExpenseEndorse, Arg.Any<string>(), reportId,
            Arg.Is<string>(d => d.StartsWith("Pre-approval manager endorsed", StringComparison.Ordinal)), manager);
        (await _sut.GetCoordinatorQueueAsync(manager, TestContext.Current.CancellationToken))
            .Should().BeEmpty("only Submitted reports wait on the first review");
    }

    [HumansFact]
    public async Task CoordinatorReject_PreapprovedReport_ByItsProgramsManager_SendsItBackToDraft_StillHoldingTheGrant()
    {
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var reportId = Guid.NewGuid();
        await SeedGrantReportAsync(reportId, submitter, grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        var manager = Guid.NewGuid();
        ManageGrant(manager, grant);

        (await _sut.CoordinatorRejectWithResultAsync(reportId, manager, "Receipt unreadable", TestContext.Current.CancellationToken))
            .Succeeded.Should().BeTrue();

        (await _sut.GetAsync(reportId, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Draft);
        await _preapprovalWrites.DidNotReceiveWithAnyArgs().ReleaseAsync(default, default, default, default, Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansFact]
    public async Task Submit_WithGrant_AfterTheDeadline_IsRefused_WithoutTouchingTheGrant()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(claimBy: GrantToday.PlusDays(-1));
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        StubNothingClaimable(submitter);

        var result = await _sut.SubmitWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalDeadlinePassed");
        result.Error.Arguments.Should().ContainSingle().Which.Should().Be("2026-05-09");
        await _preapprovalWrites.DidNotReceive().ClaimAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task Submit_WithGrant_OnTheDeadlineDay_StillClaims()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(claimBy: GrantToday);
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>()).Returns(new PreapprovalWriteResult(grant));

        var result = await _sut.SubmitWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
    }

    [HumansFact]
    public async Task Submit_WithGrant_HeldByThisReport_ResubmitsPastTheDeadline()
    {
        // A rejection does not undo a claim filed in time: the grant stays Claimed by the report
        // and ClaimAsync is idempotent for it, deadline or not.
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(claimBy: GrantToday.PlusDays(-5));
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        PutGrant(grant with { Status = GrantStatus.Claimed });
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>()).Returns(new PreapprovalWriteResult(grant));

        var result = await _sut.SubmitWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        await _preapprovalWrites.Received(1).ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansTheory]
    [InlineData(GrantStatus.Revoked, "Expenses_Validation_PreapprovalRevoked")]
    [InlineData(GrantStatus.Consumed, "Expenses_Validation_PreapprovalUnavailable")]
    [InlineData(GrantStatus.Declined, "Expenses_Validation_PreapprovalUnavailable")]
    public async Task Submit_WithGrant_NotOpen_IsRefused_WithoutClaiming(GrantStatus status, string expectedKey)
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        PutGrant(grant with { Status = status });

        var result = await _sut.SubmitWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Error!.ResourceKey.Should().Be(expectedKey);
        await _preapprovalWrites.DidNotReceive().ClaimAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrantTransition_DependencyFailure_UsesGenericFailureWithoutChangingReport(bool approve)
    {
        SetupActiveYear();
        var actor = Guid.NewGuid();
        var grant = MakeGrant();
        var id = approve ? Guid.NewGuid() : await CreateSubmittableGrantDraftAsync(actor, grant);
        if (approve)
            await SeedGrantReportAsync(id, actor, grant.Id, ExpenseReportStatus.Submitted);
        PutGrant(grant with { Status = GrantStatus.Claimed });
        var failure = new InvalidOperationException("Private persistence failure");
        _preapprovalWrites.ClaimAsync(grant.Id, id, actor, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>()).ThrowsAsync(failure);
        _preapprovalWrites.ConsumeAsync(grant.Id, id, actor, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>()).ThrowsAsync(failure);

        var result = approve
            ? await _sut.ApproveWithResultAsync(id, actor, null, null, TestContext.Current.CancellationToken)
            : await _sut.SubmitWithResultAsync(id, actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().BeNull();
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status
            .Should().Be(approve ? ExpenseReportStatus.Submitted : ExpenseReportStatus.Draft);
        (await _expenseRepo.GetLatestOutboxForReportAsync(id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task Submit_WithGrant_WhenTheClaimIsRefused_LeavesTheDraftAlone()
    {
        // Claimed by another report between the pre-check and the claim: Preapprovals is the gate.
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        PutGrant(grant with { Status = GrantStatus.Claimed });
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>())
            .Returns(new PreapprovalWriteResult(Refusal: "Grant is already held by another expense report."));

        var result = await _sut.SubmitWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalUnavailable");
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
        loaded.MaxAmount.Should().BeNull();
    }

    [HumansFact]
    public async Task Approve_WithGrant_BlankCap_KeepsTheGrantsCurrentAmount_AndConsumesIt()
    {
        var completed = TrackPreapprovalCompletions();
        SetupActiveYear();
        var actor = Guid.NewGuid();
        var grant = MakeGrant(maxAmount: 140m);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        PutGrant(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, actor, null, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.OperatorMessage);
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
        loaded.MaxAmount.Should().Be(140m, "the grant's amount as it is now, not the stamp from submit");
        await _preapprovalWrites.Received(1).ConsumeAsync(grant.Id, id, actor, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        (await _expenseRepo.GetLatestOutboxForReportAsync(id, TestContext.Current.CancellationToken))
            .Should().NotBeNull("the Holded push is queued like any approval");
        completed.Should().Equal(true);
    }

    [HumansFact]
    public async Task Approve_WithGrant_LowerCap_IsFinancesCall()
    {
        SetupActiveYear();
        var grant = MakeGrant(maxAmount: 150m);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        PutGrant(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), null, 100m, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.OperatorMessage);
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.MaxAmount.Should().Be(100m);
    }

    [HumansFact]
    public async Task Approve_WithGrant_CapAboveTheGrant_IsRefused()
    {
        SetupActiveYear();
        var grant = MakeGrant(maxAmount: 150m);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        PutGrant(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), null, 150.01m, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.OperatorMessage.Should().Contain("150.00");
        await _preapprovalWrites.DidNotReceive().ConsumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task Approve_WithGrant_AccountOverride_IsRefused()
    {
        SetupActiveYear();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted);
        PutGrant(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), OtherAccountNumber, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.OperatorMessage.Should().Contain("locked");
        await _preapprovalWrites.DidNotReceive().ConsumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        var loaded = await _sut.GetAsync(id, TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
        loaded.HoldedAccountNumber.Should().Be(GrantAccountNumber);
    }

    [HumansFact]
    public async Task Approve_WithGrant_PostingItsOwnAccount_IsNotAnOverride()
    {
        SetupActiveYear();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted);
        PutGrant(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), GrantAccountNumber, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.OperatorMessage);
    }

    [HumansFact]
    public async Task Approve_WithGrant_WhenConsumeIsRefused_LeavesTheReportPending()
    {
        SetupActiveYear();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted);
        PutGrant(grant with { Status = GrantStatus.Revoked });
        _preapprovalWrites.ConsumeAsync(grant.Id, id, Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>())
            .Returns(new PreapprovalWriteResult(Refusal: "Grant was revoked."));

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), null, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.OperatorMessage.Should().Contain("reject the report");
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
        (await _expenseRepo.GetLatestOutboxForReportAsync(id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task FinanceReject_WithGrant_LeavesTheGrantClaimed()
    {
        SetupActiveYear();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted);

        var result = await _sut.FinanceRejectWithResultAsync(id, Guid.NewGuid(), "Receipt unreadable", TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Draft);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        await _preapprovalWrites.DidNotReceive().ConsumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansFact]
    public async Task Withdraw_BeforeApproval_ReleasesTheGrant()
    {
        var completed = TrackPreapprovalCompletions();
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Submitted);
        PutGrant(grant);

        var result = await _sut.WithdrawWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Withdrawn);
        await _preapprovalWrites.Received(1).ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        completed.Should().Equal(true);
    }

    [HumansFact]
    public async Task Withdraw_AfterApproval_IsRefused_AndLeavesTheGrantConsumed()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Approved);

        var result = await _sut.WithdrawWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Flash_WithdrawFailed");
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Approved);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansFact]
    public async Task Withdraw_WhenTheReleaseIsRefused_LeavesTheReportPending()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Submitted);
        PutGrant(grant);
        _preapprovalWrites.ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>())
            .Returns(new PreapprovalWriteResult(Refusal: "Grant is not held by this report."));

        var result = await _sut.WithdrawWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task Withdraw_AfterTheGrantWasRevoked_StillWithdraws_WithoutReleasing()
    {
        // Revoke keeps the report pending; the member must still be able to take it back.
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Revoked);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Submitted);
        PutGrant(grant);

        var result = await _sut.WithdrawWithResultAsync(id, submitter, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Withdrawn);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansFact]
    public async Task UpdateDraft_RejectedDraft_DroppingTheClaimedGrant_ReleasesIt()
    {
        var completed = TrackPreapprovalCompletions();
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Draft);
        PutGrant(grant);
        StubNothingClaimable(submitter);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, CategoryAccountNumber, null, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().BeNull();
        await _preapprovalWrites.Received(1).ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
        completed.Should().Equal(true);
    }

    [HumansFact]
    public async Task UpdateDraft_RejectedDraft_SwappingTheClaimedGrant_ReleasesTheOldOne()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var old = MakeGrant(status: GrantStatus.Claimed);
        var other = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, old.Id, ExpenseReportStatus.Draft);
        StubClaimable(submitter, other);
        PutGrant(old);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, 0, null, other.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().Be(other.Id);
        await _preapprovalWrites.Received(1).ReleaseAsync(old.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansFact]
    public async Task UpdateDraft_RejectedDraft_KeepingTheClaimedGrant_ReleasesNothing()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Draft);
        PutGrant(grant);
        StubNothingClaimable(submitter);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, 0, "Fixed the receipt", grant.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansFact]
    public async Task UpdateDraft_DraftWithARevokedGrant_DropsItWithoutRelease()
    {
        // docs/features/global/preapprovals.md: a draft whose grant was revoked is not stranded.
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Revoked);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Draft);
        PutGrant(grant);
        StubNothingClaimable(submitter);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, CategoryAccountNumber, null, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().BeNull();
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>());
    }

    [HumansFact]
    public async Task UpdateDraft_RejectedDraft_WhenTheReleaseIsRefused_KeepsTheGrant()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Draft);
        PutGrant(grant);
        StubNothingClaimable(submitter);
        _preapprovalWrites.ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>(), Arg.Any<Action<Func<CancellationToken, Task>>>())
            .Returns(new PreapprovalWriteResult(Refusal: "Grant is not held by this report."));

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, CategoryAccountNumber, null, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        (await _sut.GetAsync(id, TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().Be(grant.Id);
    }
}
