using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Expenses;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Expenses;

public sealed class ExpenseReportServiceTests : ServiceTestHarness
{
    private static readonly Instant FakeNow = Instant.FromUtc(2026, 5, 10, 12, 0);

    private readonly IExpenseRepository _expenseRepo;
    private readonly IFileStorage _fileStorage;
    private readonly IBudgetService _budgetService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ExpenseReportService _sut;

    public ExpenseReportServiceTests()
        : base(FakeNow)
    {
        _expenseRepo = new ExpenseRepository(DbFactory, NullLogger<ExpenseRepository>.Instance);

        _fileStorage = Substitute.For<IFileStorage>();
        _budgetService = Substitute.For<IBudgetService>();
        _teamService = Substitute.For<ITeamService>();
        _userService = Substitute.For<IUserService>();
        _profileService = Substitute.For<IProfileService>();

        _sut = new ExpenseReportService(
            _expenseRepo,
            _fileStorage,
            _budgetService,
            _teamService,
            _userService,
            _profileService,
            AuditLog,
            Substitute.For<IHoldedClient>(),
            Clock,
            NullLogger<ExpenseReportService>.Instance);
    }

    private static UserInfo WrapInUserInfo(Profile profile) => UserInfo.Create(
        user: new User
        {
            Id = profile.UserId,
            DisplayName = profile.BurnerName,
            PreferredLanguage = "en",
            CreatedAt = FakeNow,
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);

    // ─────────────────────────────── 4.2 ─────────────────────────────────────

    [HumansFact]
    public async Task CreateDraftAsync_CreatesReport_WithDraftStatusAndZeroTotal()
    {
        var (year, category) = SetupActiveYear();
        var userId = Guid.NewGuid();

        var id = await _sut.CreateDraftAsync(userId, category.Id, "test note");

        var loaded = await _sut.GetAsync(id);
        loaded.Should().NotBeNull();
        loaded.Status.Should().Be(ExpenseReportStatus.Draft);
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
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, null!, Guid.Empty, null!, Guid.Empty);
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
    public async Task UpdateDraftWithResultAsync_ReturnsSuccess_WhenDraftUpdated()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, category.Id, "updated note");

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id);
        loaded!.Note.Should().Be("updated note");
    }

    [HumansFact]
    public async Task UpdateDraftWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var result = await _sut.UpdateDraftWithResultAsync(id, other, category.Id, null);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
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
    public async Task GetDetailViewDataAsync_ReturnsDraftSubmitterFlagsAndIban()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var report = await _sut.GetAsync(id);
        _userService.GetUserInfoAsync(submitter, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(new Profile { Id = Guid.NewGuid(), UserId = submitter, Iban = "ES9121000418450200051332" }));

        var result = await _sut.GetDetailViewDataAsync(submitter, report!);

        result.CanEdit.Should().BeTrue();
        result.CanSubmit.Should().BeTrue();
        result.CanWithdraw.Should().BeFalse();
        result.HasIban.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetDetailViewDataAsync_DeniesSubmitterActionsForOtherViewer()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var report = await _sut.GetAsync(id);

        var result = await _sut.GetDetailViewDataAsync(Guid.NewGuid(), report!);

        result.CanEdit.Should().BeFalse();
        result.CanSubmit.Should().BeFalse();
        result.CanWithdraw.Should().BeFalse();
    }

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
    public async Task AddLineWithResultAsync_ReturnsSuccess_WhenLineAdded()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var result = await _sut.AddLineWithResultAsync(id, submitter, "Supplies", 25.50m);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id);
        loaded!.Total.Should().Be(25.50m);
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);

        var result = await _sut.AddLineWithResultAsync(id, other, "x", 10m);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
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
    public async Task RemoveLineWithResultAsync_ReturnsSuccess_WhenLineRemoved()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineA = await _sut.AddLineAsync(id, submitter, "A", 10m);
        await _sut.AddLineAsync(id, submitter, "B", 20m);

        var result = await _sut.RemoveLineWithResultAsync(id, submitter, lineA);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id);
        loaded!.Total.Should().Be(20m);
    }

    [HumansFact]
    public async Task RemoveLineWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "A", 10m);

        var result = await _sut.RemoveLineWithResultAsync(id, other, lineId);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
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
    public async Task UpdateLineWithResultAsync_ReturnsSuccess_WhenLineUpdated()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "A", 10m);

        var result = await _sut.UpdateLineWithResultAsync(id, submitter, lineId, "A updated", 15m);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id);
        loaded!.Total.Should().Be(15m);
    }

    [HumansFact]
    public async Task UpdateLineWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "A", 10m);

        var result = await _sut.UpdateLineWithResultAsync(id, other, lineId, "A updated", 15m);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
    }

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

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseAttachmentUploaded,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter);
    }

    [HumansFact]
    public async Task AttachFileToLineWithResultAsync_ReturnsSuccess_WhenAttachmentUploaded()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 10m);

        await using var stream = new MemoryStream([1, 2, 3]);
        var result = await _sut.AttachFileToLineWithResultAsync(
            id, submitter, lineId, "receipt.pdf", "application/pdf", stream);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id);
        loaded!.Lines[0].AttachmentId.Should().NotBeNull();
    }

    [HumansFact]
    public async Task AttachFileToLineWithResultAsync_ReturnsFailure_WhenFileTypeUnsupported()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 10m);

        await using var stream = new MemoryStream([1, 2, 3]);
        var result = await _sut.AttachFileToLineWithResultAsync(
            id, submitter, lineId, "receipt.exe", "application/octet-stream", stream);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unsupported file type");
    }

    [HumansFact]
    public async Task TryReadAttachmentAsync_ReadsAttachmentFileFromStorage()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 10m);

        await using var stream = new MemoryStream([1, 2, 3]);
        var attachId = await _sut.AttachFileToLineAsync(
            id, submitter, lineId, "receipt.pdf", "application/pdf", stream);
        var loaded = await _sut.GetAsync(id);

        _fileStorage.TryReadAsync(
                ExpenseReportService.AttachmentKey(attachId, ".pdf"),
                Arg.Any<CancellationToken>())
            .Returns([4, 5, 6]);

        var download = await _sut.TryReadAttachmentAsync(loaded!, attachId);

        download.Should().NotBeNull();
        download.Bytes.Should().Equal(4, 5, 6);
        download.ContentType.Should().Be("application/pdf");
        download.OriginalFileName.Should().Be("receipt.pdf");
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
        await AuditLog.Received(1).LogAsync(
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
        await _fileStorage.DidNotReceiveWithAnyArgs().DeleteAsync(null!, CancellationToken.None);
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

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseSubmit,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter);
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_ReturnsSuccess_WhenReportSubmitted()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        var lineId = await _sut.AddLineAsync(id, submitter, "Item", 100m);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter));
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId);
        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var result = await _sut.SubmitWithResultAsync(id, submitter);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id);
        loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_ReturnsFailure_WhenLineHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        await _sut.AddLineAsync(id, submitter, "No attachment line", 50m);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var result = await _sut.SubmitWithResultAsync(id, submitter);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("attachment");
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

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseWithdraw,
            "ExpenseReport", reportId,
            Arg.Any<string>(),
            submitter);
    }
    [HumansFact]
    public async Task WithdrawWithResultAsync_ReturnsSuccess_WhenReportWithdrawn()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var result = await _sut.WithdrawWithResultAsync(reportId, submitter);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Withdrawn);
    }

    [HumansFact]
    public async Task WithdrawWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var result = await _sut.WithdrawWithResultAsync(reportId, other);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
    }

    // ─────────────────────────────── 4.5 ─────────────────────────────────────

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_ReturnsSuccess_WhenIbanSaved()
    {
        var submitter = Guid.NewGuid();
        _profileService
            .SetIbanAsync(submitter, "ES9121000418450200051332", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SaveSubmitterIbanWithResultAsync(submitter, "ES91 2100 0418 4502 0005 1332");

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("IBAN saved.");
        result.HasIban.Should().BeTrue();
        result.MaskedIban.Should().NotBeNullOrWhiteSpace();
    }

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_ReturnsValidationFailure_WhenIbanInvalid()
    {
        var submitter = Guid.NewGuid();
        _userService.GetUserInfoAsync(submitter, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(new Profile { Id = Guid.NewGuid(), UserId = submitter, Iban = "ES9121000418450200051332" }));

        var result = await _sut.SaveSubmitterIbanWithResultAsync(submitter, "not-an-iban");

        result.Succeeded.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
        result.Message.Should().Be("Invalid IBAN format.");
        result.HasIban.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetSubmitterIbanViewAsync_ReturnsMaskedIban_WhenProfileHasIban()
    {
        var submitter = Guid.NewGuid();
        _userService.GetUserInfoAsync(submitter, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(new Profile { Id = Guid.NewGuid(), UserId = submitter, Iban = "ES9121000418450200051332" }));

        var result = await _sut.GetSubmitterIbanViewAsync(submitter);

        result.HasIban.Should().BeTrue();
        result.MaskedIban.Should().NotBeNullOrWhiteSpace();
    }

    [HumansFact]
    public async Task GetSubmitterIbanViewAsync_ReturnsEmptyState_WhenProfileMissing()
    {
        var result = await _sut.GetSubmitterIbanViewAsync(Guid.NewGuid());

        result.HasIban.Should().BeFalse();
        result.MaskedIban.Should().BeNull();
    }

    [HumansFact]
    public async Task CategoryRequiresCoordinatorEndorsementAsync_True_WhenCategoryTeamHasCoordinator()
    {
        var teamId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var coordinatorUserId = Guid.NewGuid();

        var category = new BudgetCategory
        {
            Id = categoryId,
            BudgetGroupId = Guid.NewGuid(),
            Name = "Cat",
            TeamId = teamId,
            SortOrder = 0,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow
        };
        _budgetService.GetCategoryByIdAsync(categoryId).Returns(ToBudgetCategorySnapshot(category));
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeTeamInfo(teamId, [(coordinatorUserId, TeamMemberRole.Coordinator)]));

        var result = await _sut.CategoryRequiresCoordinatorEndorsementAsync(categoryId);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CategoryRequiresCoordinatorEndorsementAsync_False_WhenCategoryTeamHasNoCoordinator()
    {
        var teamId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        var category = new BudgetCategory
        {
            Id = categoryId,
            BudgetGroupId = Guid.NewGuid(),
            Name = "Cat",
            TeamId = teamId,
            SortOrder = 0,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow
        };
        _budgetService.GetCategoryByIdAsync(categoryId).Returns(ToBudgetCategorySnapshot(category));
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeTeamInfo(teamId, [(Guid.NewGuid(), TeamMemberRole.Member)]));

        var result = await _sut.CategoryRequiresCoordinatorEndorsementAsync(categoryId);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CategoryRequiresCoordinatorEndorsementAsync_False_WhenCategoryHasNoTeam()
    {
        var categoryId = Guid.NewGuid();
        var category = new BudgetCategory
        {
            Id = categoryId,
            BudgetGroupId = Guid.NewGuid(),
            Name = "Cat",
            TeamId = null,
            SortOrder = 0,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow
        };
        _budgetService.GetCategoryByIdAsync(categoryId).Returns(ToBudgetCategorySnapshot(category));

        var result = await _sut.CategoryRequiresCoordinatorEndorsementAsync(categoryId);
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
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(ToBudgetCategorySnapshot(category));
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

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseEndorse,
            "ExpenseReport", reportId,
            Arg.Any<string>(),
            coordinator);
    }

    [HumansFact]
    public async Task CoordinatorEndorseWithResultAsync_ReturnsSuccess_WhenEndorsed()
    {
        var (_, category) = SetupActiveYear();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);

        var result = await _sut.CoordinatorEndorseWithResultAsync(reportId, coordinator);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.CoordinatorEndorsed);
    }

    [HumansFact]
    public async Task CoordinatorEndorseWithResultAsync_ReturnsFailure_WhenNotCoordinator()
    {
        var (_, category) = SetupActiveYear();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        var nonCoordinator = Guid.NewGuid();
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(ToBudgetCategorySnapshot(category));
        _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId!.Value, nonCoordinator,
            Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CoordinatorEndorseWithResultAsync(reportId, nonCoordinator);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a coordinator");
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

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseCoordinatorReject,
            "ExpenseReport", reportId,
            Arg.Any<string>(),
            coordinator);
    }

    // ─────────────────────────────── 4.7 ─────────────────────────────────────

    [HumansFact]
    public async Task CoordinatorRejectWithResultAsync_ReturnsSuccess_WhenRejected()
    {
        var (_, category) = SetupActiveYear();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);

        var result = await _sut.CoordinatorRejectWithResultAsync(reportId, coordinator, "Missing invoice");

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task CoordinatorRejectWithResultAsync_ReturnsFailure_WhenNotCoordinator()
    {
        var (_, category) = SetupActiveYear();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        var nonCoordinator = Guid.NewGuid();
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(ToBudgetCategorySnapshot(category));
        _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId!.Value, nonCoordinator,
            Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CoordinatorRejectWithResultAsync(reportId, nonCoordinator, "Missing invoice");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a coordinator");
    }

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

        await AuditLog.Received(1).LogAsync(
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

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseApprove,
            "ExpenseReport", reportId,
            Arg.Any<string>(), actor);
        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseCategoryOverride,
            "ExpenseReport", reportId,
            Arg.Any<string>(), actor);
    }

    [HumansFact]
    public async Task ApproveWithResultAsync_ReturnsSuccess_WhenApproved()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var result = await _sut.ApproveWithResultAsync(reportId, actor, null);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
    }

    [HumansFact]
    public async Task ApproveWithResultAsync_ReturnsFailure_WhenReportMissing()
    {
        var result = await _sut.ApproveWithResultAsync(Guid.NewGuid(), Guid.NewGuid(), null);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Could not approve");
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

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseReject,
            "ExpenseReport", reportId,
            Arg.Any<string>(), actor);
    }

    // ─────────────────────────────── 4.8 ─────────────────────────────────────

    [HumansFact]
    public async Task FinanceRejectWithResultAsync_ReturnsSuccess_WhenRejected()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var result = await _sut.FinanceRejectWithResultAsync(reportId, actor, "Wrong category");

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task FinanceRejectWithResultAsync_ReturnsFailure_WhenReportMissing()
    {
        var result = await _sut.FinanceRejectWithResultAsync(Guid.NewGuid(), Guid.NewGuid(), "Wrong category");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Could not reject");
    }

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

        var flipped = await _sut.MarkSepaSentAsync([id1, id2], actor);
        flipped.Should().BeEquivalentTo([id1, id2]);

        (await _sut.GetAsync(id1))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
        (await _sut.GetAsync(id2))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
        (await _sut.GetAsync(id3))!.Status.Should().Be(ExpenseReportStatus.Submitted);

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseSepaSent, "ExpenseReport", id1,
            Arg.Any<string>(), actor);
        await AuditLog.Received(1).LogAsync(
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

        var flipped = await _sut.MarkSepaSentAsync([aId, bId], actor);

        flipped.Should().BeEquivalentTo([aId]);
        (await _sut.GetAsync(bId))!.Status.Should().Be(ExpenseReportStatus.Submitted);

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseSepaSent, "ExpenseReport", aId,
            Arg.Any<string>(), actor);
        await AuditLog.DidNotReceive().LogAsync(
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
        await AuditLog.Received(1).LogAsync(
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
            Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetCoordinatorQueueAsync(Guid.NewGuid());
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCoordinatorQueueAsync_ReturnsSubmittedReportsForCoordinatedCategories()
    {
        var (_, category) = SetupActiveYear();
        var coordinatorUserId = Guid.NewGuid();
        var teamId = category.TeamId!.Value;
        var yearId = Guid.NewGuid();

        var submittedId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        await SeedReportWithStatus(submittedId, Guid.NewGuid(), category.Id, yearId,
            ExpenseReportStatus.Submitted);
        await SeedReportWithStatus(draftId, Guid.NewGuid(), category.Id, yearId,
            ExpenseReportStatus.Draft);

        // Also seed a Submitted report in a category the user does NOT coordinate.
        var otherCategoryId = Guid.NewGuid();
        var otherSubmittedId = Guid.NewGuid();
        await SeedReportWithStatus(otherSubmittedId, Guid.NewGuid(), otherCategoryId, yearId,
            ExpenseReportStatus.Submitted);

        _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinatorUserId,
            Arg.Any<CancellationToken>()).Returns([teamId]);

        var result = await _sut.GetCoordinatorQueueAsync(coordinatorUserId);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(submittedId);
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
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(ToBudgetCategorySnapshot(category));

        return (year, category);
    }


    private static BudgetCategorySnapshot ToBudgetCategorySnapshot(BudgetCategory category) =>
        new(
            category.Id,
            category.BudgetGroupId,
            category.Name,
            category.AllocatedAmount,
            category.ExpenditureType,
            category.TeamId,
            category.SortOrder,
            category.BudgetGroup is null
                ? null
                : new BudgetCategoryGroupSnapshot(
                    category.BudgetGroup.Id,
                    category.BudgetGroup.BudgetYearId,
                    category.BudgetGroup.Name,
                    category.BudgetGroup.IsRestricted,
                    category.BudgetGroup.IsTicketingGroup,
                    category.BudgetGroup.BudgetYear is null
                        ? null
                        : new BudgetCategoryYearSnapshot(
                            category.BudgetGroup.BudgetYear.Id,
                            category.BudgetGroup.BudgetYear.Year,
                            category.BudgetGroup.BudgetYear.Name,
                            category.BudgetGroup.BudgetYear.IsDeleted)),
            []);
    private void SetupUserAndProfile(Guid userId, string displayName, string iban)
    {
        var nameParts = displayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var firstName = nameParts.Length > 0 ? nameParts[0] : displayName;
        var lastName = nameParts.Length > 1 ? nameParts[1] : "Tester";

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BurnerName = displayName,
                FirstName = firstName,
                LastName = lastName,
                Iban = iban
            }));
    }

    private static TeamInfo MakeTeamInfo(Guid teamId,
        IReadOnlyList<(Guid UserId, TeamMemberRole Role)> members) =>
        new(
            Id: teamId,
            Name: "Test Team",
            Description: null,
            Slug: "test-team",
            IsActive: true,
            IsSystemTeam: false,
            SystemTeamType: SystemTeamType.None,
            RequiresApproval: false,
            IsPublicPage: false,
            IsHidden: false,
            IsPromotedToDirectory: false,
            CreatedAt: FakeNow,
            Members: members
                .Select(m => new TeamMemberInfo(
                    TeamMemberId: Guid.NewGuid(),
                    UserId: m.UserId,
                    DisplayName: "Member",
                    Email: null,
                    ProfilePictureUrl: null,
                    Role: m.Role,
                    JoinedAt: FakeNow))
                .ToList());

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
        _budgetService.GetCategoryByIdAsync(categoryId).Returns(ToBudgetCategorySnapshot(cat));
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
        await using var ctx = await DbFactory.CreateDbContextAsync();
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
