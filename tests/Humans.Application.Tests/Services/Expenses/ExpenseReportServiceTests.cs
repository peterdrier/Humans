using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Expenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Expenses;

public class ExpenseReportServiceTests
{
    private static readonly Instant FakeNow = Instant.FromUtc(2026, 5, 10, 12, 0);

    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly IExpenseRepository _expenseRepo;
    private readonly IFileStorage _fileStorage;
    private readonly IBudgetService _budgetService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLogService;
    private readonly ExpenseReportService _sut;

    public ExpenseReportServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbContextFactory(options);
        _expenseRepo = new ExpenseRepository(_factory, NullLogger<ExpenseRepository>.Instance);

        _fileStorage = Substitute.For<IFileStorage>();
        _budgetService = Substitute.For<IBudgetService>();
        _teamService = Substitute.For<ITeamService>();
        _userService = Substitute.For<IUserService>();
        _auditLogService = Substitute.For<IAuditLogService>();

        _sut = new ExpenseReportService(
            _expenseRepo,
            _fileStorage,
            _budgetService,
            _teamService,
            _userService,
            _auditLogService,
            Substitute.For<IHoldedClient>(),
            new FakeClock(FakeNow),
            NullLogger<ExpenseReportService>.Instance);
    }

    private static UserInfo WrapInUserInfo(Profile profile) => UserInfo.Create(
        user: new User
        {
            Id = profile.UserId,
            DisplayName = profile.BurnerName ?? "",
            PreferredLanguage = "en",
            CreatedAt = FakeNow,
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        },
        userEmails: Array.Empty<UserEmail>(),
        eventParticipations: Array.Empty<EventParticipation>(),
        externalLogins: Array.Empty<(string, string)>(),
        profile: profile,
        contactFields: Array.Empty<ContactField>(),
        profileLanguages: Array.Empty<ProfileLanguage>(),
        volunteerHistory: Array.Empty<VolunteerHistoryEntry>(),
        communicationPreferences: Array.Empty<CommunicationPreference>());

    // ─────────────────────────────── 4.2 ─────────────────────────────────────

    [HumansFact]
    public async Task CreateDraftAsync_CreatesReport_WithDraftStatusAndZeroTotal()
    {
        var (year, category) = SetupActiveYear();
        var userId = Guid.NewGuid();

        var id = await _sut.CreateDraftAsync(userId, category.Id, "test note");

        var loaded = await _sut.GetAsync(id);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
        loaded.Total.Should().Be(0m);
        loaded.SubmitterUserId.Should().Be(userId);
        loaded.BudgetCategoryId.Should().Be(category.Id);
        loaded.BudgetYearId.Should().Be(year.Id);
        loaded.Note.Should().Be("test note");
    }

    [HumansFact]
    public async Task CreateDraftAsync_Throws_WhenNoActiveYear()
    {
        _budgetService.GetActiveYearAsync().Returns((BudgetYear?)null);

        var act = async () => await _sut.CreateDraftAsync(Guid.NewGuid(), Guid.NewGuid(), null);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active budget year*");
    }

    [HumansFact]
    public async Task CreateDraftAsync_DoesNotAudit_OnCreate()
    {
        var (_, category) = SetupActiveYear();
        await _sut.CreateDraftAsync(Guid.NewGuid(), category.Id, null);

        // No audit on mere draft creation
        await _auditLogService.DidNotReceiveWithAnyArgs().LogAsync(
            default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task UpdateDraftAsync_Throws_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var act = async () => await _sut.UpdateDraftAsync(id, other, category.Id, null);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task GetAsync_ReturnsNull_ForMissingId()
    {
        var result = await _sut.GetAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetForSubmitterAsync_ScopesToSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        await _sut.CreateDraftAsync(me, category.Id, null);
        await _sut.CreateDraftAsync(other, category.Id, null);

        var mine = await _sut.GetForSubmitterAsync(me);
        mine.Should().HaveCount(1);
        mine[0].SubmitterUserId.Should().Be(me);
    }

    // ─────────────────────────────── 4.3 ─────────────────────────────────────

    [HumansFact]
    public async Task AddLineAsync_AddsLine_AndUpdatesTotal()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var lineId = await _sut.AddLineAsync(id, submitter, "Supplies", 25.50m);

        var loaded = await _sut.GetAsync(id);
        loaded!.Total.Should().Be(25.50m);
        loaded.Lines.Should().HaveCount(1);
        loaded.Lines[0].Id.Should().Be(lineId);
        loaded.Lines[0].Description.Should().Be("Supplies");
        loaded.Lines[0].Amount.Should().Be(25.50m);
    }

    [HumansFact]
    public async Task AddLineAsync_Throws_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var act = async () => await _sut.AddLineAsync(id, other, "x", 10m);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task RemoveLineAsync_RemovesLine_AndRecomputesTotal()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineA = await _sut.AddLineAsync(id, submitter, "A", 10m);
        await _sut.AddLineAsync(id, submitter, "B", 20m);

        await _sut.RemoveLineAsync(id, submitter, lineA);

        var loaded = await _sut.GetAsync(id);
        loaded!.Total.Should().Be(20m);
        loaded.Lines.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task UpdateLineAsync_UpdatesAmount_AndRecomputesTotal()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "A", 10m);

        await _sut.UpdateLineAsync(id, submitter, lineId, "A updated", 15m);

        var loaded = await _sut.GetAsync(id);
        loaded!.Total.Should().Be(15m);
        loaded.Lines[0].Description.Should().Be("A updated");
    }

    // ─────────────────── AttachFileToLineAsync / RemoveAttachmentFromLineAsync ───

    [HumansFact]
    public async Task AttachFileToLineAsync_StoresFile_CreatesRow_LinksLine_Audits()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 10m);

        await using var stream = new MemoryStream([1, 2, 3]);
        var attachId = await _sut.AttachFileToLineAsync(
            id, submitter, lineId, "receipt.pdf", "application/pdf", stream);

        attachId.Should().NotBe(Guid.Empty);
        var loaded = await _sut.GetAsync(id);
        loaded!.Lines[0].AttachmentId.Should().Be(attachId);

        await _fileStorage.Received(1).SaveAsync(
            $"uploads/expense-attachments/{attachId}.pdf",
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseAttachmentUploaded,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter);
    }

    [HumansFact]
    public async Task AttachFileToLineAsync_Throws_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 10m);

        await using var stream = new MemoryStream([1, 2, 3]);
        var act = async () => await _sut.AttachFileToLineAsync(
            id, other, lineId, "receipt.pdf", "application/pdf", stream);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task AttachFileToLineAsync_Throws_WhenLineDoesNotBelongToReport()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        await _sut.AddLineAsync(id, submitter, "Item", 10m);
        var wrongLineId = Guid.NewGuid();

        await using var stream = new MemoryStream([1, 2, 3]);
        var act = async () => await _sut.AttachFileToLineAsync(
            id, submitter, wrongLineId, "receipt.pdf", "application/pdf", stream);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task RemoveAttachmentFromLineAsync_UnlinksAndDeletesFile_Audits()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 10m);

        // Seed attachment directly through repo
        var attach = MakeAttachment(submitter);
        await _expenseRepo.AddAttachmentAsync(attach);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attach.Id);

        await _sut.RemoveAttachmentFromLineAsync(id, submitter, lineId);

        var loaded = await _sut.GetAsync(id);
        loaded!.Lines[0].AttachmentId.Should().BeNull();

        await _fileStorage.Received(1).DeleteAsync(
            $"uploads/expense-attachments/{attach.Id}{attach.Extension}",
            Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseAttachmentRemoved,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter);
    }

    [HumansFact]
    public async Task RemoveAttachmentFromLineAsync_IsIdempotent_WhenNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 10m);

        // No attachment on the line — should not throw
        var act = async () => await _sut.RemoveAttachmentFromLineAsync(id, submitter, lineId);
        await act.Should().NotThrowAsync();
        await _fileStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    // ─────────────────────────────── 4.4 ─────────────────────────────────────

    [HumansFact]
    public async Task SubmitAsync_FlipsToSubmitted_SnapshotsPayeeData()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 100m);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter));
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId);

        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var ok = await _sut.SubmitAsync(id, submitter);

        ok.Should().BeTrue();
        var loaded = await _sut.GetAsync(id);
        loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
        loaded.PayeeName.Should().Be("Alice Tester");
        loaded.PayeeIban.Should().Be("ES9121000418450200051332");
        loaded.SubmittedAt.Should().Be(FakeNow);
    }

    [HumansFact]
    public async Task SubmitAsync_Throws_WhenNoLines()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var act = async () => await _sut.SubmitAsync(id, submitter);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one line*");
    }

    [HumansFact]
    public async Task SubmitAsync_Throws_WhenLineHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        await _sut.AddLineAsync(id, submitter, "No attachment line", 50m);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var act = async () => await _sut.SubmitAsync(id, submitter);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*attachment*");
    }

    [HumansFact]
    public async Task SubmitAsync_Throws_WhenSubmitterHasNoIban()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 50m);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter));
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId);

        // Profile with no IBAN
        _userService.GetUserInfoAsync(submitter, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(new Profile { Id = Guid.NewGuid(), UserId = submitter }));

        var act = async () => await _sut.SubmitAsync(id, submitter);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IBAN*");
    }

    [HumansFact]
    public async Task SubmitAsync_WritesAudit_AfterSave()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 100m);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter));
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId);
        SetupUserAndProfile(submitter, "Alice", "ES9121000418450200051332");

        await _sut.SubmitAsync(id, submitter);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseSubmit,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter);
    }

    [HumansFact]
    public async Task WithdrawAsync_FlipsToWithdrawn()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var ok = await _sut.WithdrawAsync(reportId, submitter);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Withdrawn);
    }

    [HumansFact]
    public async Task WithdrawAsync_WritesAudit_AfterSave()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        await _sut.WithdrawAsync(reportId, submitter);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseWithdraw,
            "ExpenseReport", reportId,
            Arg.Any<string>(),
            submitter);
    }

    // ─────────────────────────────── 4.5 ─────────────────────────────────────

    [HumansFact]
    public async Task CategoryRequiresCoordinatorEndorsementAsync_AlwaysReturnsFalse()
    {
        var result = await _sut.CategoryRequiresCoordinatorEndorsementAsync(Guid.NewGuid());
        result.Should().BeFalse();
    }

    // ─────────────────────────────── 4.6 ─────────────────────────────────────

    [HumansFact]
    public async Task CoordinatorEndorseAsync_FlipsToCoordinatorEndorsed()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);

        var ok = await _sut.CoordinatorEndorseAsync(reportId, coordinator);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.CoordinatorEndorsed);
        loaded.CoordinatorEndorsedByUserId.Should().Be(coordinator);
    }

    [HumansFact]
    public async Task CoordinatorEndorseAsync_Throws_WhenNotCoordinator()
    {
        var (_, category) = SetupActiveYear();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var nonCoordinator = Guid.NewGuid();
        _budgetService.GetCategoryByIdAsync(category.Id)
            .Returns(category);
        _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId!.Value, nonCoordinator,
            Arg.Any<CancellationToken>()).Returns(false);

        var act = async () => await _sut.CoordinatorEndorseAsync(reportId, nonCoordinator);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task CoordinatorEndorseAsync_WritesAudit()
    {
        var (_, category) = SetupActiveYear();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);

        await _sut.CoordinatorEndorseAsync(reportId, coordinator);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseEndorse,
            "ExpenseReport", reportId,
            Arg.Any<string>(),
            coordinator);
    }

    [HumansFact]
    public async Task CoordinatorRejectAsync_ReturnsToSubmitted_And_Audits()
    {
        var (_, category) = SetupActiveYear();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);

        var ok = await _sut.CoordinatorRejectAsync(reportId, coordinator, "Missing invoice");
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
        loaded.LastRejectionReason.Should().Be("Missing invoice");

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseCoordinatorReject,
            "ExpenseReport", reportId,
            Arg.Any<string>(),
            coordinator);
    }

    // ─────────────────────────────── 4.7 ─────────────────────────────────────

    [HumansFact]
    public async Task ApproveAsync_FlipsToApproved_AndAudits()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var ok = await _sut.ApproveAsync(reportId, actor, null);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
        loaded.ApprovedByUserId.Should().Be(actor);
        loaded.ApprovedAt.Should().Be(FakeNow);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseApprove,
            "ExpenseReport", reportId,
            Arg.Any<string>(),
            actor);
    }

    [HumansFact]
    public async Task ApproveAsync_WithOverrideCategory_AuditsBoth()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var overrideCatId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        await _sut.ApproveAsync(reportId, actor, overrideCatId);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseApprove,
            "ExpenseReport", reportId,
            Arg.Any<string>(), actor);
        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseCategoryOverride,
            "ExpenseReport", reportId,
            Arg.Any<string>(), actor);
    }

    [HumansFact]
    public async Task FinanceRejectAsync_ReturnsToDraft_AndAudits()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var ok = await _sut.FinanceRejectAsync(reportId, actor, "Wrong category");
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
        loaded.LastRejectionReason.Should().Be("Wrong category");

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseReject,
            "ExpenseReport", reportId,
            Arg.Any<string>(), actor);
    }

    // ─────────────────────────────── 4.8 ─────────────────────────────────────

    [HumansFact]
    public async Task MarkSepaSentAsync_FlipsBatch_AndAuditsEach()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var yearId = Guid.NewGuid();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        await SeedReportWithStatus(id1, Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Approved);
        await SeedReportWithStatus(id2, Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Approved);
        await SeedReportWithStatus(id3, Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Submitted); // not Approved

        var flipped = await _sut.MarkSepaSentAsync(new[] { id1, id2 }, actor);
        flipped.Should().BeEquivalentTo(new[] { id1, id2 });

        (await _sut.GetAsync(id1))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
        (await _sut.GetAsync(id2))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
        (await _sut.GetAsync(id3))!.Status.Should().Be(ExpenseReportStatus.Submitted);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseSepaSent, "ExpenseReport", id1,
            Arg.Any<string>(), actor);
        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseSepaSent, "ExpenseReport", id2,
            Arg.Any<string>(), actor);
    }

    [HumansFact]
    public async Task MarkSepaSentAsync_DoesNotAudit_NonApprovedIdsInInput()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var yearId = Guid.NewGuid();
        var aId = Guid.NewGuid(); // Approved → will flip
        var bId = Guid.NewGuid(); // Submitted → will be skipped by repo
        await SeedReportWithStatus(aId, Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Approved);
        await SeedReportWithStatus(bId, Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Submitted);

        var flipped = await _sut.MarkSepaSentAsync(new[] { aId, bId }, actor);

        flipped.Should().BeEquivalentTo(new[] { aId });
        (await _sut.GetAsync(bId))!.Status.Should().Be(ExpenseReportStatus.Submitted);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpenseSepaSent, "ExpenseReport", aId,
            Arg.Any<string>(), actor);
        await _auditLogService.DidNotReceive().LogAsync(
            AuditAction.ExpenseSepaSent, "ExpenseReport", bId,
            Arg.Any<string>(), actor);
    }

    [HumansFact]
    public async Task MarkPaidAsync_FlipsToPaid_AndAuditsJob()
    {
        var (_, category) = SetupActiveYear();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.SepaSent);

        var ok = await _sut.MarkPaidAsync(reportId);
        ok.Should().BeTrue();

        (await _sut.GetAsync(reportId))!.Status.Should().Be(ExpenseReportStatus.Paid);
        await _auditLogService.Received(1).LogAsync(
            AuditAction.ExpensePaid, "ExpenseReport", reportId,
            Arg.Any<string>(),
            "ExpensePaidJob");
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_ReturnsNonDraftNonWithdrawn()
    {
        var (_, category) = SetupActiveYear();
        var yearId = Guid.NewGuid();
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Draft);
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Submitted);
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Approved);
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Withdrawn);

        var queue = await _sut.GetReviewQueueAsync();
        queue.Should().HaveCount(2);
        queue.Should().OnlyContain(r =>
            r.Status != ExpenseReportStatus.Draft && r.Status != ExpenseReportStatus.Withdrawn);
    }

    [HumansFact]
    public async Task GetCoordinatorQueueAsync_ReturnsEmptyWhenNoTeams()
    {
        _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(Arg.Any<Guid>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<Guid>());

        var result = await _sut.GetCoordinatorQueueAsync(Guid.NewGuid());
        result.Should().BeEmpty();
    }

    // ─────────────────────────── Helpers ─────────────────────────────────────

    private (BudgetYear Year, BudgetCategory Category) SetupActiveYear()
    {
        var teamId = Guid.NewGuid();
        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = Guid.NewGuid(),
            Name = "Test Category",
            TeamId = teamId,
            SortOrder = 0,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow
        };
        var group = new BudgetGroup
        {
            Id = category.BudgetGroupId,
            BudgetYearId = Guid.NewGuid(),
            Name = "Test Group",
            SortOrder = 0,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow
        };
        group.Categories.Add(category);

        var year = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = "2026",
            Name = "Test Year 2026",
            Status = BudgetYearStatus.Active,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow
        };
        year.Groups.Add(group);

        _budgetService.GetActiveYearAsync().Returns(year);
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(category);

        return (year, category);
    }

    private void SetupUserAndProfile(Guid userId, string displayName, string iban)
    {
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, DisplayName = displayName });
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(new Profile { Id = Guid.NewGuid(), UserId = userId, Iban = iban }));
    }

    private void SetupCoordinatorAuthz(Guid categoryId, Guid teamId, Guid coordinatorUserId)
    {
        var cat = new BudgetCategory
        {
            Id = categoryId,
            BudgetGroupId = Guid.NewGuid(),
            Name = "Cat",
            TeamId = teamId,
            SortOrder = 0,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow
        };
        _budgetService.GetCategoryByIdAsync(categoryId).Returns(cat);
        _teamService.IsUserCoordinatorOfTeamAsync(teamId, coordinatorUserId,
            Arg.Any<CancellationToken>()).Returns(true);
    }

    private async Task SeedReportWithStatus(
        Guid reportId, Guid submitter, Guid categoryId, Guid yearId,
        ExpenseReportStatus status)
    {
        var now = Instant.FromUtc(2026, 5, 1, 0, 0);
        var report = new ExpenseReport
        {
            Id = reportId,
            SubmitterUserId = submitter,
            BudgetCategoryId = categoryId,
            BudgetYearId = yearId,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.ExpenseReports.Add(report);
        await ctx.SaveChangesAsync();
    }

    private static ExpenseAttachment MakeAttachment(Guid uploaderId) => new()
    {
        Id = Guid.NewGuid(),
        OriginalFileName = "receipt.pdf",
        Extension = ".pdf",
        ContentType = "application/pdf",
        SizeBytes = 1024,
        UploadedByUserId = uploaderId,
        UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
    };
}
