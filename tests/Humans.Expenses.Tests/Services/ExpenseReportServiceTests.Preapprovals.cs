using AwesomeAssertions;
using Xunit;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
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
    private const int GrantAccountNumber = 62900500;
    private const string GrantAccountId = "acc-grant";
    /// <summary>FakeNow is 2026-05-10 12:00 UTC: 14:00 in Madrid, so today is the 10th.</summary>
    private static readonly LocalDate GrantToday = new(2026, 5, 10);

    private static GrantOption MakeGrant(
        Guid? id = null, GrantStatus status = GrantStatus.Open, decimal maxAmount = 150m, LocalDate? claimBy = null) =>
        new(id ?? Guid.NewGuid(), "Burner bus", "Driver", maxAmount, GrantAccountNumber, GrantAccountId,
            claimBy ?? GrantToday.PlusDays(10), status);

    private static GrantStamp StampOf(GrantOption g, decimal? maxAmount = null) =>
        new(maxAmount ?? g.MaxAmount, g.HoldedAccountNumber, g.HoldedAccountId, g.ClaimBy);

    /// <summary>The grant is the submitter's to file against today, and reads back by id.</summary>
    private void StubClaimable(Guid submitter, params GrantOption[] grants)
    {
        _preapprovals.ListClaimableForUserAsync(submitter, Arg.Any<CancellationToken>()).Returns(grants.ToList());
        foreach (var g in grants)
            _preapprovals.GetGrantAsync(g.Id, Arg.Any<CancellationToken>()).Returns(g);
    }

    private async Task<Guid> CreateGrantDraftAsync(Guid submitter, GrantOption grant)
    {
        StubClaimable(submitter, grant);
        var result = await _sut.CreateDraftAsync(submitter, submitter, 0, "Project X", grant.Id, Xunit.TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        return result.ReportId!.Value;
    }

    /// <summary>A submittable draft on the grant: one receipt line with its file, a payee profile.</summary>
    private async Task<Guid> CreateSubmittableGrantDraftAsync(Guid submitter, GrantOption grant)
    {
        var id = await CreateGrantDraftAsync(submitter, grant);
        var lineId = (await _sut.AddLineWithResultAsync(id, submitter, false, "Fuel", 120m, ct: Xunit.TestContext.Current.CancellationToken)).LineId!.Value;
        await _expenseRepo.ReplaceLineAttachmentAsync(id, lineId, MakeAttachment(submitter), Xunit.TestContext.Current.CancellationToken);
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
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    // ─── Draft ─────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task CreateDraft_WithGrant_BooksToTheGrantsAccount_WithNoCategory()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();

        var id = await CreateGrantDraftAsync(submitter, grant);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.PreapprovalGrantId.Should().Be(grant.Id);
        loaded.HoldedAccountNumber.Should().Be(GrantAccountNumber);
        loaded.HoldedAccountId.Should().Be(GrantAccountId);
        loaded.BudgetCategoryId.Should().BeNull("a pre-approved report skips coordinator endorsement");
        loaded.BudgetYearId.Should().BeNull();
        loaded.MaxAmount.Should().BeNull("the cap is stamped at submit, not at draft");
        await _holdedFinance.DidNotReceive().ListExpenseAccountsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateDraft_WithGrant_RefusesOneTheSubmitterCannotClaim()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var someoneElsesGrant = MakeGrant();
        _preapprovals.ListClaimableForUserAsync(submitter, Arg.Any<CancellationToken>()).Returns([]);
        _preapprovals.GetGrantAsync(someoneElsesGrant.Id, Arg.Any<CancellationToken>()).Returns(someoneElsesGrant);

        var result = await _sut.CreateDraftAsync(submitter, submitter, CategoryAccountNumber, "Project X", someoneElsesGrant.Id, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalNotClaimable");
        (await _sut.GetForSubmitterAsync(submitter, Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateDraft_OnBehalf_UsesTheMembersGrants_NotTheAdmins()
    {
        SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var adminsGrant = MakeGrant();
        StubClaimable(admin, adminsGrant);
        _preapprovals.ListClaimableForUserAsync(member, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.CreateDraftAsync(member, admin, 0, "Project X", adminsGrant.Id, Xunit.TestContext.Current.CancellationToken);

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
        _preapprovals.ListClaimableForUserAsync(submitter, Arg.Any<CancellationToken>()).Returns([]);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, 0, "fixed", grant.Id, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.PreapprovalGrantId.Should().Be(grant.Id);
        loaded.Note.Should().Be("fixed");
    }

    [HumansFact]
    public async Task UpdateDraft_DroppingTheGrant_BooksFromTheAccountAgain()
    {
        var (year, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await CreateGrantDraftAsync(submitter, MakeGrant());

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, CategoryAccountNumber, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
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
        _preapprovals.GetGrantAsync(held.Id, Arg.Any<CancellationToken>())
            .Returns(held with { HoldedAccountNumber = 62900600, HoldedAccountId = "acc-new-program" });

        var result = await _sut.UpdateDraftWithResultAsync(
            id, admin, true, GrantAccountNumber, "Corrected subject", held.Id, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
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

        var swapped = await _sut.UpdateDraftWithResultAsync(id, admin, true, GrantAccountNumber, null, other.Id, Xunit.TestContext.Current.CancellationToken);
        var rebooked = await _sut.UpdateDraftWithResultAsync(id, admin, true, OtherAccountNumber, null, held.Id, Xunit.TestContext.Current.CancellationToken);
        var noteOnly = await _sut.UpdateDraftWithResultAsync(id, admin, true, GrantAccountNumber, "note", held.Id, Xunit.TestContext.Current.CancellationToken);

        swapped.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalLocked");
        rebooked.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalLocked");
        noteOnly.Succeeded.Should().BeTrue(noteOnly.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.PreapprovalGrantId.Should().Be(held.Id);
        loaded.HoldedAccountNumber.Should().Be(GrantAccountNumber);
        loaded.Note.Should().Be("note");
    }

    // ─── Submit ────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Submit_WithGrant_ClaimsIt_AndRestampsAccountAndCapFromTheGrant()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(maxAmount: 150m);
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        // The program changed since the draft: what the grant says now is what the report carries.
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>())
            .Returns(new GrantStamp(130m, 62900510, "acc-grant-2", grant.ClaimBy));

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
        loaded.HoldedAccountNumber.Should().Be(62900510);
        loaded.HoldedAccountId.Should().Be("acc-grant-2");
        loaded.MaxAmount.Should().Be(130m);
        loaded.Payable.Should().Be(120m, "receipts under the cap pay in full");
        loaded.BudgetCategoryId.Should().BeNull();
    }

    [HumansFact]
    public async Task Submit_WithGrant_SkipsCoordinatorEndorsement()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>()).Returns(StampOf(grant));
        (await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken)).Succeeded.Should().BeTrue();

        var endorse = await _sut.CoordinatorEndorseWithResultAsync(id, Guid.NewGuid(), false, null, Xunit.TestContext.Current.CancellationToken);

        endorse.Succeeded.Should().BeFalse();
        endorse.Error!.OperatorMessage.Should().Contain("not booked to a budget category");
        (await _sut.GetReviewQueueAsync(Guid.NewGuid(), isFinanceAdmin: true, Xunit.TestContext.Current.CancellationToken))
            .Should().ContainSingle(r => r.Id == id, "it waits on Finance directly");
    }

    [HumansFact]
    public async Task Submit_WithGrant_AfterTheDeadline_IsRefused_WithoutTouchingTheGrant()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(claimBy: GrantToday.PlusDays(-1));
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalDeadlinePassed");
        result.Error.Arguments.Should().ContainSingle().Which.Should().Be("2026-05-09");
        await _preapprovalWrites.DidNotReceive().ClaimAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task Submit_WithGrant_OnTheDeadlineDay_StillClaims()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(claimBy: GrantToday);
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>()).Returns(StampOf(grant));

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

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
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>()).Returns(StampOf(grant));

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        await _preapprovalWrites.Received(1).ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(GrantStatus.Revoked, "Expenses_Validation_PreapprovalRevoked")]
    [Xunit.InlineData(GrantStatus.Consumed, "Expenses_Validation_PreapprovalUnavailable")]
    [Xunit.InlineData(GrantStatus.Declined, "Expenses_Validation_PreapprovalUnavailable")]
    public async Task Submit_WithGrant_NotOpen_IsRefused_WithoutClaiming(GrantStatus status, string expectedKey)
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = status });

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Error!.ResourceKey.Should().Be(expectedKey);
        await _preapprovalWrites.DidNotReceive().ClaimAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task Submit_WithGrant_WhenTheClaimIsRefused_LeavesTheDraftAlone()
    {
        // Claimed by another report between the pre-check and the claim: Preapprovals is the gate.
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = await CreateSubmittableGrantDraftAsync(submitter, grant);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });
        _preapprovalWrites.ClaimAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Grant is already held by another expense report."));

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.ResourceKey.Should().Be("Expenses_Validation_PreapprovalUnavailable");
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
        loaded.MaxAmount.Should().BeNull();
    }

    // ─── Approve ───────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Approve_WithGrant_BlankCap_KeepsTheGrantsCurrentAmount_AndConsumesIt()
    {
        SetupActiveYear();
        var actor = Guid.NewGuid();
        var grant = MakeGrant(maxAmount: 140m);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, actor, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.OperatorMessage);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
        loaded.MaxAmount.Should().Be(140m, "the grant's amount as it is now, not the stamp from submit");
        await _preapprovalWrites.Received(1).ConsumeAsync(grant.Id, id, actor, Arg.Any<CancellationToken>());
        (await _expenseRepo.GetLatestOutboxForReportAsync(id, Xunit.TestContext.Current.CancellationToken))
            .Should().NotBeNull("the Holded push is queued like any approval");
    }

    [HumansFact]
    public async Task Approve_WithGrant_LowerCap_IsFinancesCall()
    {
        SetupActiveYear();
        var grant = MakeGrant(maxAmount: 150m);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), null, 100m, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.OperatorMessage);
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.MaxAmount.Should().Be(100m);
    }

    [HumansFact]
    public async Task Approve_WithGrant_CapAboveTheGrant_IsRefused()
    {
        SetupActiveYear();
        var grant = MakeGrant(maxAmount: 150m);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted, maxAmount: 150m);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), null, 150.01m, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.OperatorMessage.Should().Contain("150.00");
        await _preapprovalWrites.DidNotReceive().ConsumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task Approve_WithGrant_AccountOverride_IsRefused()
    {
        SetupActiveYear();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), OtherAccountNumber, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.OperatorMessage.Should().Contain("locked");
        await _preapprovalWrites.DidNotReceive().ConsumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
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
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Claimed });

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), GrantAccountNumber, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.OperatorMessage);
    }

    [HumansFact]
    public async Task Approve_WithGrant_WhenConsumeIsRefused_LeavesTheReportPending()
    {
        SetupActiveYear();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant with { Status = GrantStatus.Revoked });
        _preapprovalWrites.ConsumeAsync(grant.Id, id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Grant was revoked."));

        var result = await _sut.ApproveWithResultAsync(id, Guid.NewGuid(), null, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error!.OperatorMessage.Should().Contain("reject the report");
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
        (await _expenseRepo.GetLatestOutboxForReportAsync(id, Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ─── Reject / withdraw ─────────────────────────────────────────────────────

    [HumansFact]
    public async Task FinanceReject_WithGrant_LeavesTheGrantClaimed()
    {
        SetupActiveYear();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, Guid.NewGuid(), grant.Id, ExpenseReportStatus.Submitted);

        var result = await _sut.FinanceRejectWithResultAsync(id, Guid.NewGuid(), "Receipt unreadable", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Draft);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _preapprovalWrites.DidNotReceive().ConsumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Withdraw_BeforeApproval_ReleasesTheGrant()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Submitted);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        var result = await _sut.WithdrawWithResultAsync(id, submitter, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Withdrawn);
        await _preapprovalWrites.Received(1).ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Withdraw_AfterApproval_LeavesTheGrantConsumed()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant();
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Approved);

        var result = await _sut.WithdrawWithResultAsync(id, submitter, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Withdraw_WhenTheReleaseIsRefused_LeavesTheReportPending()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Submitted);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);
        _preapprovalWrites.ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Grant is not held by this report."));

        var result = await _sut.WithdrawWithResultAsync(id, submitter, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
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
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        var result = await _sut.WithdrawWithResultAsync(id, submitter, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Withdrawn);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ─── Rejected draft: the grant it still holds ───────────────────────────────

    [HumansFact]
    public async Task UpdateDraft_RejectedDraft_DroppingTheClaimedGrant_ReleasesIt()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Draft);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);
        _preapprovals.ListClaimableForUserAsync(submitter, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, CategoryAccountNumber, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().BeNull();
        await _preapprovalWrites.Received(1).ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>());
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
        _preapprovals.GetGrantAsync(old.Id, Arg.Any<CancellationToken>()).Returns(old);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, 0, null, other.Id, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().Be(other.Id);
        await _preapprovalWrites.Received(1).ReleaseAsync(old.Id, id, submitter, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateDraft_RejectedDraft_KeepingTheClaimedGrant_ReleasesNothing()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Draft);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);
        _preapprovals.ListClaimableForUserAsync(submitter, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, 0, "Fixed the receipt", grant.Id, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);
        _preapprovals.ListClaimableForUserAsync(submitter, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, CategoryAccountNumber, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error?.ResourceKey);
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().BeNull();
        await _preapprovalWrites.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateDraft_RejectedDraft_WhenTheReleaseIsRefused_KeepsTheGrant()
    {
        SetupActiveYear();
        var submitter = Guid.NewGuid();
        var grant = MakeGrant(status: GrantStatus.Claimed);
        var id = Guid.NewGuid();
        await SeedGrantReportAsync(id, submitter, grant.Id, ExpenseReportStatus.Draft);
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);
        _preapprovals.ListClaimableForUserAsync(submitter, Arg.Any<CancellationToken>()).Returns([]);
        _preapprovalWrites.ReleaseAsync(grant.Id, id, submitter, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Grant is not held by this report."));

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, CategoryAccountNumber, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.PreapprovalGrantId.Should().Be(grant.Id);
    }
}
