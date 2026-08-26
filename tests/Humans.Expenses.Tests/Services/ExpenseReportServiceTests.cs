using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Expenses.Data;
using Humans.Teams.Contracts;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Microsoft.Extensions.Options;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Users.Contracts;

namespace Humans.Expenses.Tests.Services;

/// <summary>
/// Owns its three fixtures rather than deriving from <c>ServiceTestHarness</c>, which it did
/// while it lived in <c>Humans.Application.Tests</c>. That harness is built around an in-memory
/// <c>UsersDbContext</c> and this test never touched it — only the audit substitute, the clock
/// and the section-context options below. Inheriting it here would have meant granting a section
/// test project <c>InternalsVisibleTo</c> on <c>UsersDbContext</c>, which is the boundary the
/// G5 split exists to draw (nobodies-collective/Humans#866).
/// </summary>
public sealed class ExpenseReportServiceTests
{
    private static readonly Instant FakeNow = Instant.FromUtc(2026, 5, 10, 12, 0);

    private readonly IAuditLogService AuditLog = Substitute.For<IAuditLogService>();
    private readonly FakeClock Clock = new(FakeNow);

    private static DbContextOptions<TContext> NewSectionDbOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private readonly IExpenseRepository _expenseRepo;
    private readonly IFileStorage _fileStorage;
    private readonly IBudgetServiceRead _budgetService;
    private readonly ITeamServiceRead _teamService;
    private readonly IUserService _userService;
    private readonly IHoldedClient _holdedClient = Substitute.For<IHoldedClient>();
    private readonly IHoldedFinanceService _holdedFinance = Substitute.For<IHoldedFinanceService>();
    private readonly ExpenseReportService _sut;

    private readonly DbContextOptions<ExpensesDbContext> _expensesOptions =
        NewSectionDbOptions<ExpensesDbContext>();

    public ExpenseReportServiceTests()
    {
        _expenseRepo = new ExpenseRepository(new TestDbContextFactory<ExpensesDbContext>(_expensesOptions));

        // The drain self-guards on the API key now (the job used to), so the substitute has to
        // claim a key or DrainHoldedOutboxAsync returns before touching anything.
        _holdedClient.IsConfigured.Returns(true);

        _fileStorage = Substitute.For<IFileStorage>();
        _budgetService = Substitute.For<IBudgetServiceRead>();
        _teamService = Substitute.For<ITeamServiceRead>();
        _userService = Substitute.For<IUserService>();

        _sut = new ExpenseReportService(
            _expenseRepo,
            _fileStorage,
            _budgetService,
            _teamService,
            _userService,
            AuditLog,
            _holdedClient,
            _holdedFinance,
            Clock,
            NullLogger<ExpenseReportService>.Instance,
            Options.Create(new TravelReimbursementConfig()));
    }

    private static UserInfo WrapInUserInfo(Guid userId, ProfileInfo profile) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            DisplayName = profile.BurnerName,
            PreferredLanguage = "en",
            CreatedAt = FakeNow,
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        communicationPreferences: []);

    // ─────────────────────────────── 4.2 ─────────────────────────────────────

    [HumansFact]
    public async Task CreateDraftAsync_CreatesReport_WithDraftStatusAndZeroTotal()
    {
        var (year, category) = SetupActiveYear();
        var userId = Guid.NewGuid();

        var id = await _sut.CreateDraftAsync(userId, userId, category.Id, "test note", Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
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
        _budgetService.GetActiveYearAsync().Returns((BudgetYearDetail?)null);

        var submitter = Guid.NewGuid();
        var act = async () => await _sut.CreateDraftAsync(submitter, submitter, Guid.NewGuid(), null, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active budget year*");
    }

    [HumansFact]
    public async Task CreateDraftAsync_DoesNotAudit_OnCreate()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

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
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var act = async () => await _sut.UpdateDraftAsync(id, other, false, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task UpdateDraftWithResultAsync_ReturnsSuccess_WhenDraftUpdated()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.UpdateDraftWithResultAsync(id, submitter, false, category.Id, "updated note", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Note.Should().Be("updated note");
    }

    [HumansFact]
    public async Task UpdateDraftWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.UpdateDraftWithResultAsync(id, other, false, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
    }

    [HumansFact]
    public async Task GetAsync_ReturnsNull_ForMissingId()
    {
        var result = await _sut.GetAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetForSubmitterAsync_ScopesToSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        await _sut.CreateDraftAsync(me, me, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.CreateDraftAsync(other, other, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var mine = await _sut.GetForSubmitterAsync(me, Xunit.TestContext.Current.CancellationToken);
        mine.Should().HaveCount(1);
        mine[0].SubmitterUserId.Should().Be(me);
    }

    // ─────────────────────────────── 4.3 ─────────────────────────────────────

    [HumansFact]
    public async Task AddLineAsync_AddsLine_AndUpdatesTotal()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var lineId = await _sut.AddLineAsync(id, submitter, false, "Supplies", 25.50m, ct: Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
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
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var act = async () => await _sut.AddLineAsync(id, other, false, "x", 10m, ct: Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_ReturnsSuccess_WhenLineAdded()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.AddLineWithResultAsync(id, submitter, false, "Supplies", 25.50m, ct: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(25.50m);
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.AddLineWithResultAsync(id, other, false, "x", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
    }

    [HumansFact]
    public async Task RemoveLineAsync_RemovesLine_AndRecomputesTotal()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineA = await _sut.AddLineAsync(id, submitter, false, "A", 10m, ct: Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(id, submitter, false, "B", 20m, ct: Xunit.TestContext.Current.CancellationToken);

        await _sut.RemoveLineAsync(id, submitter, false, lineA, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(20m);
        loaded.Lines.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task RemoveLineWithResultAsync_ReturnsSuccess_WhenLineRemoved()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineA = await _sut.AddLineAsync(id, submitter, false, "A", 10m, ct: Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(id, submitter, false, "B", 20m, ct: Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.RemoveLineWithResultAsync(id, submitter, false, lineA, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(20m);
    }

    [HumansFact]
    public async Task RemoveLineWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "A", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.RemoveLineWithResultAsync(id, other, false, lineId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
    }

    [HumansFact]
    public async Task UpdateLineAsync_UpdatesAmount_AndRecomputesTotal()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "A", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await _sut.UpdateLineAsync(id, submitter, false, lineId, "A updated", 15m, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(15m);
        loaded.Lines[0].Description.Should().Be("A updated");
    }

    // ─────────────────── AttachFileToLineAsync / RemoveAttachmentFromLineAsync ───

    [HumansFact]
    public async Task UpdateLineWithResultAsync_ReturnsSuccess_WhenLineUpdated()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "A", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.UpdateLineWithResultAsync(id, submitter, false, lineId, "A updated", 15m, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(15m);
    }

    [HumansFact]
    public async Task UpdateLineWithResultAsync_ReturnsFailure_WhenNotSubmitter()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "A", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.UpdateLineWithResultAsync(id, other, false, lineId, "A updated", 15m, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
    }

    [HumansFact]
    public async Task UpdateLineWithResultAsync_ReturnsFailure_AndKeepsAmount_ForTravelLine()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddMileageLineWithResultAsync(id, submitter, "Berlin", "Barcelona", 100m, Xunit.TestContext.Current.CancellationToken);
        var line = (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Lines[0];
        var originalAmount = line.Amount;

        // Editing a computed travel line would bypass the receipt waiver — must be rejected.
        var result = await _sut.UpdateLineWithResultAsync(id, submitter, false, line.Id, "hand-edited", 9999m, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Travel lines");
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Lines[0].Amount.Should().Be(originalAmount);
    }

    [HumansFact]
    public async Task AttachFileToLineAsync_StoresFile_CreatesRow_LinksLine_Audits()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream([1, 2, 3]);
        var attachId = await _sut.AttachFileToLineAsync(
            id, submitter, false, lineId, "receipt.pdf", "application/pdf", stream, Xunit.TestContext.Current.CancellationToken);

        attachId.Should().NotBe(Guid.Empty);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines[0].AttachmentId.Should().Be(attachId);

        await _fileStorage.Received(1).SaveAsync(
            $"uploads/expense-attachments/{attachId}.pdf",
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseAttachmentUploaded,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter,
            submitter,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task AttachFileToLineWithResultAsync_ReturnsSuccess_WhenAttachmentUploaded()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream([1, 2, 3]);
        var result = await _sut.AttachFileToLineWithResultAsync(
            id, submitter, false, lineId, "receipt.pdf", "application/pdf", stream, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines[0].AttachmentId.Should().NotBeNull();
    }

    [HumansFact]
    public async Task AttachFileToLineWithResultAsync_ReturnsFailure_WhenFileTypeUnsupported()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream([1, 2, 3]);
        var result = await _sut.AttachFileToLineWithResultAsync(
            id, submitter, false, lineId, "receipt.exe", "application/octet-stream", stream, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unsupported file type");
    }

    [HumansTheory]
    [Xunit.InlineData("receipt.exe", "application/octet-stream", 3, "Unsupported file type")]
    [Xunit.InlineData("receipt.pdf", "application/pdf", 0, "Please select a file")]
    [Xunit.InlineData("receipt.pdf", "application/pdf", 21 * 1024 * 1024, "File too large")] // AttachmentMaxBytes is 20 MB
    public async Task AttachFileToLineWithResultAsync_LogsWarning_NoStackTrace_ForUserInputRejection(
        string fileName, string contentType, int byteCount, string expectedMessage)
    {
        var logger = new CapturingLogger<ExpenseReportService>();
        var sut = new ExpenseReportService(
            _expenseRepo, _fileStorage, _budgetService, _teamService, _userService,
            AuditLog, _holdedClient, _holdedFinance, Clock, logger,
            Options.Create(new TravelReimbursementConfig()));

        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream(new byte[byteCount]);
        var result = await sut.AttachFileToLineWithResultAsync(
            id, submitter, false, lineId, fileName, contentType, stream, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedMessage,
            because: "the user-facing message is unchanged by the log-level reclassification");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        var warning = logger.Entries.Single(e => e.Level == LogLevel.Warning);
        warning.Exception.Should().BeNull("a rejected upload is user input, not a system failure");
        warning.Message.Should().Contain(expectedMessage,
            because: "RunMutationAsync appends the rejection as the {Reason} property");
        warning.Message.Should().Contain(lineId.ToString(),
            because: "the caller's structured identifiers must survive into the warning");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_LogsError_WithStackTrace_WhenRepositoryReportsFailure()
    {
        var logger = new CapturingLogger<ExpenseReportService>();

        // A repository returning false is a genuine persistence fault, not user input — it must
        // keep its Error level and stack trace. Only GetByIdAsync is delegated to the real
        // in-memory repository so the draft-editable guard passes and the failure lands on AddLine.
        var failingRepo = Substitute.For<IExpenseRepository>();
        failingRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _expenseRepo.GetByIdAsync(call.Arg<Guid>(), call.Arg<CancellationToken>()));
        failingRepo.AddLineAsync(Arg.Any<Guid>(), Arg.Any<ExpenseLine>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = new ExpenseReportService(
            failingRepo, _fileStorage, _budgetService, _teamService, _userService,
            AuditLog, _holdedClient, _holdedFinance, Clock, logger,
            Options.Create(new TravelReimbursementConfig()));

        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await sut.AddLineWithResultAsync(
            id, submitter, false, "Supplies", 25m, ct: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
        var error = logger.Entries.Single(e => e.Level == LogLevel.Error);
        error.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Failed to add line.");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public async Task TryReadAttachmentAsync_ReadsAttachmentFileFromStorage()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream([1, 2, 3]);
        var attachId = await _sut.AttachFileToLineAsync(
            id, submitter, false, lineId, "receipt.pdf", "application/pdf", stream, Xunit.TestContext.Current.CancellationToken);
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);

        _fileStorage.TryReadAsync(
                ExpenseReportService.AttachmentKey(attachId, ".pdf"),
                Arg.Any<CancellationToken>())
            .Returns([4, 5, 6]);

        var download = await _sut.TryReadAttachmentAsync(loaded!, attachId, Xunit.TestContext.Current.CancellationToken);

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
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream([1, 2, 3]);
        var act = async () => await _sut.AttachFileToLineAsync(
            id, other, false, lineId, "receipt.pdf", "application/pdf", stream, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task AttachFileToLineAsync_Throws_WhenLineDoesNotBelongToReport()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);
        var wrongLineId = Guid.NewGuid();

        await using var stream = new MemoryStream([1, 2, 3]);
        var act = async () => await _sut.AttachFileToLineAsync(
            id, submitter, false, wrongLineId, "receipt.pdf", "application/pdf", stream, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task RemoveAttachmentFromLineAsync_UnlinksAndDeletesFile_Audits()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        // Seed attachment directly through repo
        var attach = MakeAttachment(submitter);
        await _expenseRepo.AddAttachmentAsync(attach, Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attach.Id, Xunit.TestContext.Current.CancellationToken);

        await _sut.RemoveAttachmentFromLineAsync(id, submitter, false, lineId, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines[0].AttachmentId.Should().BeNull();

        await _fileStorage.Received(1).DeleteAsync(
            $"uploads/expense-attachments/{attach.Id}{attach.Extension}",
            Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseAttachmentRemoved,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter,
            submitter,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task RemoveAttachmentFromLineAsync_IsIdempotent_WhenNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        // No attachment on the line — should not throw
        var act = async () => await _sut.RemoveAttachmentFromLineAsync(id, submitter, false, lineId, Xunit.TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync();
        await _fileStorage.DidNotReceiveWithAnyArgs().DeleteAsync(null!, Arg.Any<CancellationToken>());
    }

    // ───────────────── Invoice lines + proof rows ────────────────────────────

    [HumansFact]
    public async Task AddLineAsync_ProofRow_UnderInvoiceLine_ExcludedFromTotal()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var invoiceId = await _sut.AddLineAsync(id, submitter, false, "Invoice 2026-042", 1000m, ExpenseLineType.Invoice, ct: Xunit.TestContext.Current.CancellationToken);

        await _sut.AddLineAsync(id, submitter, false, "Timber", 400m, parentLineId: invoiceId, ct: Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(1000m);
        loaded.Lines.Should().HaveCount(2);
        loaded.Lines.Single(l => l.ParentLineId is not null).ParentLineId.Should().Be(invoiceId);
    }

    [HumansFact]
    public async Task AddLineAsync_ProofRow_Throws_WhenParentIsNotInvoice()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var receiptId = await _sut.AddLineAsync(id, submitter, false, "Plain receipt", 50m, ct: Xunit.TestContext.Current.CancellationToken);

        var act = async () => await _sut.AddLineAsync(id, submitter, false, "Proof", 10m, parentLineId: receiptId, ct: Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invoice line*");
    }

    [HumansFact]
    public async Task AddLineAsync_ProofRow_Throws_WhenParentMissing()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var act = async () => await _sut.AddLineAsync(id, submitter, false, "Proof", 10m, parentLineId: Guid.NewGuid(), ct: Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Parent line not found*");
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_WithFile_CreatesLineAndAttachment_InOneCall()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        using var content = new MemoryStream([1, 2, 3]);
        var result = await _sut.AddLineWithResultAsync(
            id, submitter, false, "Timber", 40m,
            file: new ExpenseFileUpload("receipt.pdf", "application/pdf", content),
            ct: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.LineId.Should().NotBeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Single().AttachmentId.Should().NotBeNull();
        loaded.Lines.Single().Attachment!.OriginalFileName.Should().Be("receipt.pdf");
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_RollsBackLine_WhenUploadFailsAfterCreation()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        _fileStorage.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));

        using var content = new MemoryStream([1, 2, 3]);
        var result = await _sut.AddLineWithResultAsync(
            id, submitter, false, "Timber", 40m,
            file: new ExpenseFileUpload("receipt.pdf", "application/pdf", content),
            ct: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.LineId.Should().BeNull();
        // The form retries the whole add — a leftover line would duplicate on retry.
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Should().BeEmpty();
        loaded.Total.Should().Be(0m);
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_WithBadFile_CreatesNothing()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        using var content = new MemoryStream([1, 2, 3]);
        var result = await _sut.AddLineWithResultAsync(
            id, submitter, false, "Timber", 40m,
            file: new ExpenseFileUpload("virus.exe", "application/octet-stream", content),
            ct: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.LineId.Should().BeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddLineWithResultAsync_RejectsTravelTypes()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.AddLineWithResultAsync(id, submitter, false, "Trip", 26m, ExpenseLineType.Mileage, ct: Xunit.TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("receipt and invoice");
    }

    [HumansFact]
    public async Task RemoveLineAsync_InvoiceLine_RemovesItsProofRows_AndTheirFiles()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var invoiceId = await _sut.AddLineAsync(id, submitter, false, "Invoice", 1000m, ExpenseLineType.Invoice, ct: Xunit.TestContext.Current.CancellationToken);
        var proofId = await _sut.AddLineAsync(id, submitter, false, "Timber", 400m, parentLineId: invoiceId, ct: Xunit.TestContext.Current.CancellationToken);

        var proofAttachment = MakeAttachment(submitter);
        await _expenseRepo.AddAttachmentAsync(proofAttachment, Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(proofId, proofAttachment.Id, Xunit.TestContext.Current.CancellationToken);

        await _sut.RemoveLineAsync(id, submitter, false, invoiceId, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Should().BeEmpty();
        loaded.Total.Should().Be(0m);
        await _fileStorage.Received(1).DeleteAsync(
            $"uploads/expense-attachments/{proofAttachment.Id}{proofAttachment.Extension}",
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitAsync_Throws_WhenInvoiceLineHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(id, submitter, false, "Invoice, no file", 1000m, ExpenseLineType.Invoice, ct: Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var act = async () => await _sut.SubmitAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*attachment*");
    }

    [HumansFact]
    public async Task SubmitAsync_Throws_WhenProofRowHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var invoiceId = await _sut.AddLineAsync(id, submitter, false, "Invoice", 1000m, ExpenseLineType.Invoice, ct: Xunit.TestContext.Current.CancellationToken);
        var invoiceFile = MakeAttachment(submitter);
        await _expenseRepo.AddAttachmentAsync(invoiceFile, Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(invoiceId, invoiceFile.Id, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(id, submitter, false, "Proof, no file", 400m, parentLineId: invoiceId, ct: Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var act = async () => await _sut.SubmitAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*attachment*");
    }

    // ─────────────────────────────── 4.4 ─────────────────────────────────────

    [HumansFact]
    public async Task SubmitAsync_FlipsToSubmitted_SnapshotsPayeeData()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 100m, ct: Xunit.TestContext.Current.CancellationToken);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter), Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId, Xunit.TestContext.Current.CancellationToken);

        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var ok = await _sut.SubmitAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
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
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var act = async () => await _sut.SubmitAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one line*");
    }

    [HumansFact]
    public async Task SubmitAsync_Throws_WhenLineHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(id, submitter, false, "No attachment line", 50m, ct: Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var act = async () => await _sut.SubmitAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*attachment*");
    }

    [HumansFact]
    public async Task SubmitAsync_Throws_WhenSubmitterHasNoIban()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 50m, ct: Xunit.TestContext.Current.CancellationToken);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter), Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId, Xunit.TestContext.Current.CancellationToken);

        // Profile with no IBAN
        _userService.GetUserInfoAsync(submitter, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(submitter, UserFixtures.Profile()));

        var act = async () => await _sut.SubmitAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IBAN*");
    }

    [HumansFact]
    public async Task SubmitAsync_WritesAudit_AfterSave()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 100m, ct: Xunit.TestContext.Current.CancellationToken);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter), Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId, Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Alice", "ES9121000418450200051332");

        await _sut.SubmitAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseSubmit,
            "ExpenseReport", id,
            Arg.Any<string>(),
            submitter,
            submitter,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_ReturnsSuccess_WhenReportSubmitted()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Item", 100m, ct: Xunit.TestContext.Current.CancellationToken);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter), Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId, Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_ReturnsFailure_WhenLineHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(id, submitter, false, "No attachment line", 50m, ct: Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("attachment");
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_Succeeds_WithOnlyMileageLine_NoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddMileageLineWithResultAsync(id, submitter, "Berlin", "Barcelona", 100m, Xunit.TestContext.Current.CancellationToken);
        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_Fails_WhenReceiptLineHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineWithResultAsync(id, submitter, false, "Tent", 50m, ct: Xunit.TestContext.Current.CancellationToken); // Receipt line, no attachment
        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var result = await _sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_LogsWarning_WithReportId_NoException_WhenValidationFails()
    {
        var logger = new CapturingLogger<ExpenseReportService>();
        var sut = new ExpenseReportService(
            _expenseRepo, _fileStorage, _budgetService, _teamService, _userService,
            AuditLog, _holdedClient, _holdedFinance, Clock, logger,
            Options.Create(new TravelReimbursementConfig()));

        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        await sut.AddLineAsync(id, submitter, false, "No attachment line", 50m, ct: Xunit.TestContext.Current.CancellationToken); // Receipt line, no attachment
        SetupUserAndProfile(submitter, "Bob", "ES1234");

        var result = await sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning,
            because: "a validation rejection is an expected, user-driven outcome, not a fault");
        var warning = logger.Entries.Single(e => e.Level == LogLevel.Warning);
        warning.Exception.Should().BeNull("no stack trace should be logged for a validation rejection");
        warning.Message.Should().Contain(id.ToString(),
            because: "the caller's structured identifiers (report ID) must survive into the warning");
        warning.Message.Should().Contain("attachment");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_LogsError_WithException_ForGenuineFault()
    {
        var logger = new CapturingLogger<ExpenseReportService>();
        var sut = new ExpenseReportService(
            _expenseRepo, _fileStorage, _budgetService, _teamService, _userService,
            AuditLog, _holdedClient, _holdedFinance, Clock, logger,
            Options.Create(new TravelReimbursementConfig()));

        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await sut.AddLineAsync(id, submitter, false, "Item", 50m, ct: Xunit.TestContext.Current.CancellationToken);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(submitter), Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId, Xunit.TestContext.Current.CancellationToken);

        // A dependency throwing plain InvalidOperationException for a genuine runtime fault —
        // NOT the service's own ExpenseValidationException — must still log at Error with the
        // exception attached, not be misclassified as a validation rejection.
        _userService.GetUserInfoAsync(submitter, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("IUserService: profile cache not initialized"));

        var result = await sut.SubmitWithResultAsync(id, submitter, false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error,
            because: "a dependency fault (even one thrown as InvalidOperationException) is not a validation rejection");
        var error = logger.Entries.Single(e => e.Level == LogLevel.Error);
        error.Exception.Should().BeOfType<InvalidOperationException>();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public async Task WithdrawAsync_FlipsToWithdrawn()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var ok = await _sut.WithdrawAsync(reportId, submitter, Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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

        await _sut.WithdrawAsync(reportId, submitter, Xunit.TestContext.Current.CancellationToken);

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

        var result = await _sut.WithdrawWithResultAsync(reportId, submitter, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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

        var result = await _sut.WithdrawWithResultAsync(reportId, other, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Only the submitter");
    }

    // ─────────────────────────────── 4.5 ─────────────────────────────────────

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_ReturnsSuccess_WhenIbanSaved()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Draft);
        _userService
            .SetProfileIbanAsync(submitter, "ES9121000418450200051332", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SaveSubmitterIbanWithResultAsync(reportId, submitter, "ES91 2100 0418 4502 0005 1332", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("IBAN saved.");
        await AuditLog.Received(1).LogAsync(
            AuditAction.IbanSet,
            AuditEntityTypes.Profile,
            submitter,
            "IBAN set",
            submitter,
            submitter,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_ReturnsValidationFailure_WhenIbanInvalid()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Draft);

        var result = await _sut.SaveSubmitterIbanWithResultAsync(reportId, submitter, "not-an-iban", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
        result.Message.Should().Be("Invalid IBAN format.");
    }

    // ─────────────────── Acting on a member's behalf ─────────────────────────

    [HumansFact]
    public async Task CreateDraftAsync_OnBehalf_MakesTheMemberTheSubmitter_AndAudits()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        SetupUserAndProfile(member, "Dani Member", "ES9121000418450200051332");

        var id = await _sut.CreateDraftAsync(member, admin, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.SubmitterUserId.Should().Be(member, "the report belongs to the member, not the admin who filed it");
        // relatedEntityId is what carries the entry into the *member's* GDPR export — the entity is
        // the report and the actor is the admin, so without it the row never reaches its subject.
        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseCreatedOnBehalf,
            AuditEntityTypes.Report, id,
            Arg.Is<string>(d => d.Contains("Dani Member")),
            admin,
            member,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task UpdateDraftAsync_OnBehalf_Succeeds_OnAnEndorsedReport()
    {
        // A finance admin's edit window covers the statuses a report can still be corrected in,
        // and correcting one does not send it back a step — the endorsement stands.
        var (year, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, member, category.Id, year.Id,
            ExpenseReportStatus.CoordinatorEndorsed);

        await _sut.UpdateDraftAsync(reportId, admin, true, category.Id, "corrected", Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.Note.Should().Be("corrected");
        loaded.Status.Should().Be(ExpenseReportStatus.CoordinatorEndorsed);
    }

    [HumansFact]
    public async Task UpdateDraftAsync_OnAPendingReport_KeepsItsOwnBudgetYear()
    {
        // The report belongs to a year that is no longer the active one. Re-resolving its header
        // through the active year would silently move last year's accounting into this year.
        var (_, _) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var oldYearId = Guid.NewGuid();
        var oldCategoryId = Guid.NewGuid();
        _budgetService.GetCategoryByIdAsync(oldCategoryId)
            .Returns(MakeCategorySnapshot(oldCategoryId, null, "Last Year Category", null, oldYearId));
        await SeedReportWithStatus(reportId, member, oldCategoryId, oldYearId,
            ExpenseReportStatus.Submitted);

        await _sut.UpdateDraftAsync(reportId, admin, true, oldCategoryId, "corrected", Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.Note.Should().Be("corrected");
        loaded.BudgetYearId.Should().Be(oldYearId);
        loaded.BudgetCategoryId.Should().Be(oldCategoryId);
    }

    [HumansFact]
    public async Task UpdateDraftAsync_OnAPendingReport_RejectsACategoryFromAnotherYear()
    {
        var (_, activeCategory) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var oldYearId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, member, Guid.NewGuid(), oldYearId,
            ExpenseReportStatus.Submitted);

        // activeCategory belongs to the active year, not to this report's year.
        var act = async () => await _sut.UpdateDraftAsync(
            reportId, admin, true, activeCategory.Id, "reclassified", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different budget year*");
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.BudgetYearId.Should().Be(oldYearId);
    }

    [HumansFact]
    public async Task UpdateDraftAsync_OnADraft_StillStampsTheActiveYear()
    {
        // Pre-existing behaviour, deliberately unchanged: a draft is not booked anywhere yet.
        var (year, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, submitter, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Draft);

        await _sut.UpdateDraftAsync(reportId, submitter, false, category.Id, "note", Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.BudgetYearId.Should().Be(year.Id);
    }

    [HumansFact]
    public async Task EveryOnBehalfEdit_WritesAnAuditEntry_NamingTheMember()
    {
        // An admin changing somebody else's report is an action taken on their behalf, so each
        // header and line change owes them a trail — not just the create and the submit.
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        SetupUserAndProfile(member, "Dani Member", "ES9121000418450200051332");
        var id = await _sut.CreateDraftAsync(member, member, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        await _sut.UpdateDraftAsync(id, admin, true, category.Id, "corrected", Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, admin, true, "Supplies", 25m, ct: Xunit.TestContext.Current.CancellationToken);
        await _sut.UpdateLineAsync(id, admin, true, lineId, "Supplies (corrected)", 30m, Xunit.TestContext.Current.CancellationToken);
        await _sut.RemoveLineAsync(id, admin, true, lineId, Xunit.TestContext.Current.CancellationToken);

        await AuditLog.Received(4).LogAsync(
            AuditAction.ExpenseEditedOnBehalf,
            AuditEntityTypes.Report, id,
            Arg.Is<string>(d => d.Contains("Dani Member")),
            admin,
            member,
            AuditEntityTypes.User);
    }

    [HumansTheory]
    [Xunit.InlineData("Updated header")]
    [Xunit.InlineData("Added line")]
    [Xunit.InlineData("Updated line")]
    [Xunit.InlineData("Removed line")]
    public async Task OnBehalfEditAudit_SaysWhatChanged(string expectedOpener)
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        SetupUserAndProfile(member, "Dani Member", "ES9121000418450200051332");
        var id = await _sut.CreateDraftAsync(member, member, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        await _sut.UpdateDraftAsync(id, admin, true, category.Id, "corrected", Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, admin, true, "Supplies", 25m, ct: Xunit.TestContext.Current.CancellationToken);
        await _sut.UpdateLineAsync(id, admin, true, lineId, "Supplies (corrected)", 30m, Xunit.TestContext.Current.CancellationToken);
        await _sut.RemoveLineAsync(id, admin, true, lineId, Xunit.TestContext.Current.CancellationToken);

        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseEditedOnBehalf,
            AuditEntityTypes.Report, id,
            Arg.Is<string>(d => d.StartsWith(expectedOpener)),
            admin,
            member,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task EditingYourOwnReport_WritesNoOnBehalfAudit()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, submitter, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        await _sut.UpdateDraftAsync(id, submitter, false, category.Id, "my note", Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, submitter, false, "Supplies", 25m, ct: Xunit.TestContext.Current.CancellationToken);
        await _sut.UpdateLineAsync(id, submitter, false, lineId, "Supplies", 30m, Xunit.TestContext.Current.CancellationToken);
        await _sut.RemoveLineAsync(id, submitter, false, lineId, Xunit.TestContext.Current.CancellationToken);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.ExpenseEditedOnBehalf,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddLineAsync_OnBehalf_Succeeds_ForFinanceAdmin()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(member, member, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var lineId = await _sut.AddLineAsync(id, admin, true, "Supplies", 25m, ct: Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Should().ContainSingle(l => l.Id == lineId);
        loaded.Total.Should().Be(25m);
    }

    [HumansFact]
    public async Task AddLineAsync_OnBehalf_Throws_ForANonFinanceAdmin()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(member, member, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var act = async () => await _sut.AddLineAsync(id, other, false, "Supplies", 25m, ct: Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task SubmitAsync_OnBehalf_SnapshotsTheMembersProfile_NotTheActors()
    {
        // The payee is whoever the report belongs to. Reading the acting admin's profile here
        // would pay the admin's account for the member's receipts.
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(member, admin, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var lineId = await _sut.AddLineAsync(id, admin, true, "Item", 100m, ct: Xunit.TestContext.Current.CancellationToken);
        var attachId = await _expenseRepo.AddAttachmentAsync(MakeAttachment(admin), Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetLineAttachmentAsync(lineId, attachId, Xunit.TestContext.Current.CancellationToken);

        SetupUserAndProfile(member, "Dani Member", "ES9121000418450200051332");
        SetupUserAndProfile(admin, "Ada Admin", "ES7100302053091234567895");

        var ok = await _sut.SubmitAsync(id, admin, true, Xunit.TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        var loaded = await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken);
        loaded!.PayeeName.Should().Be("Dani Member");
        loaded.PayeeIban.Should().Be("ES9121000418450200051332");

        // Entity is the report and the actor is the admin, so the related id is the only thing
        // carrying the submission into Dani's own GDPR slice.
        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseSubmit,
            "ExpenseReport", id,
            Arg.Any<string>(),
            admin,
            member,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_OnBehalf_AuditsTheIbanUnmasked_NamingTheMember()
    {
        // memory/code/audit-pii-subject-allowed.md: the entry's subject is the member, so the
        // account number belongs in it — tracing a wrongly-typed IBAN needs the value typed.
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, member, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Draft);
        SetupUserAndProfile(member, "Dani Member", "ES9121000418450200051332");
        _userService
            .SetProfileIbanAsync(member, "ES9121000418450200051332", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SaveSubmitterIbanWithResultAsync(reportId, admin, "ES91 2100 0418 4502 0005 1332", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        // The entry's entity type is Profile and its actor is the admin, so relatedEntityId is the
        // only thing tying the row carrying Dani's raw IBAN to Dani's own GDPR export.
        await AuditLog.Received(1).LogAsync(
            AuditAction.IbanSet,
            AuditEntityTypes.Profile,
            member,
            "IBAN set for Dani Member to ES9121000418450200051332",
            admin,
            member,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task AttachFileToLineAsync_OnBehalf_RelatesTheAuditToTheMember_NotTheAdmin()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(member, admin, category.Id, null, Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(id, admin, true, "Item", 10m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream([1, 2, 3]);
        await _sut.AttachFileToLineAsync(
            id, admin, true, lineId, "receipt.pdf", "application/pdf", stream, Xunit.TestContext.Current.CancellationToken);

        // Entity is the report and the actor is the admin, so without the related id the upload
        // never reaches the member's own GDPR slice.
        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpenseAttachmentUploaded,
            "ExpenseReport", id,
            Arg.Any<string>(),
            admin,
            member,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_RefreshesThePayeeSnapshot_OnASubmittedReport()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, member, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted, payeeIban: "ES1000750000010000000000");
        SetupUserAndProfile(member, "Dani Member", "ES9121000418450200051332");
        _userService
            .SetProfileIbanAsync(member, "ES9121000418450200051332", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SaveSubmitterIbanWithResultAsync(
            reportId, admin, "ES91 2100 0418 4502 0005 1332", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.PayeeIban.Should().Be("ES9121000418450200051332");
        await AuditLog.Received(1).LogAsync(
            AuditAction.ExpensePayeeIbanUpdated,
            "ExpenseReport", reportId,
            "Payee IBAN updated for Dani Member to ES9121000418450200051332.",
            admin,
            member,
            AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_LeavesThePayeeSnapshotAlone_OnADraft()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, member, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Draft);
        _userService
            .SetProfileIbanAsync(member, "ES9121000418450200051332", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SaveSubmitterIbanWithResultAsync(
            reportId, member, "ES91 2100 0418 4502 0005 1332", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        // Submit takes the snapshot; a draft has none to correct.
        (await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken))!.PayeeIban.Should().BeEmpty();
        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.ExpensePayeeIbanUpdated,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_LeavesThePayeeSnapshotAlone_OnAnApprovedReport()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, member, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Approved, payeeIban: "ES1000750000010000000000");
        _userService
            .SetProfileIbanAsync(member, "ES9121000418450200051332", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SaveSubmitterIbanWithResultAsync(
            reportId, member, "ES91 2100 0418 4502 0005 1332", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        // Already booked into Holded — the snapshot is history, not a live payment detail.
        (await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken))!
            .PayeeIban.Should().Be("ES1000750000010000000000");
    }

    [HumansFact]
    public async Task SaveSubmitterIbanWithResultAsync_RefusesTheRemoval_OnASubmittedReport()
    {
        var (_, category) = SetupActiveYear();
        var member = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, member, category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted, payeeIban: "ES1000750000010000000000");

        var result = await _sut.SaveSubmitterIbanWithResultAsync(
            reportId, member, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
        result.Message.Should().Contain("needs an IBAN");
        // Neither half of the change lands — profile and snapshot stay in agreement.
        await _userService.DidNotReceive().SetProfileIbanAsync(
            Arg.Any<Guid>(), null, Arg.Any<CancellationToken>());
        (await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken))!
            .PayeeIban.Should().Be("ES1000750000010000000000");
    }

    [HumansFact]
    public async Task CategoryRequiresCoordinatorEndorsementAsync_True_WhenCategoryTeamHasCoordinator()
    {
        var teamId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var coordinatorUserId = Guid.NewGuid();

        _budgetService.GetCategoryByIdAsync(categoryId)
            .Returns(MakeCategorySnapshot(categoryId, teamId));
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeTeamInfo(teamId, [(coordinatorUserId, TeamMemberRole.Coordinator)]));

        var result = await _sut.CategoryRequiresCoordinatorEndorsementAsync(categoryId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CategoryRequiresCoordinatorEndorsementAsync_False_WhenCategoryTeamHasNoCoordinator()
    {
        var teamId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        _budgetService.GetCategoryByIdAsync(categoryId)
            .Returns(MakeCategorySnapshot(categoryId, teamId));
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeTeamInfo(teamId, [(Guid.NewGuid(), TeamMemberRole.Member)]));

        var result = await _sut.CategoryRequiresCoordinatorEndorsementAsync(categoryId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CategoryRequiresCoordinatorEndorsementAsync_False_WhenCategoryHasNoTeam()
    {
        var categoryId = Guid.NewGuid();
        _budgetService.GetCategoryByIdAsync(categoryId)
            .Returns(MakeCategorySnapshot(categoryId, teamId: null));

        var result = await _sut.CategoryRequiresCoordinatorEndorsementAsync(categoryId, Xunit.TestContext.Current.CancellationToken);
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

        var ok = await _sut.CoordinatorEndorseAsync(reportId, coordinator, null, Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(category);
        _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId!.Value, nonCoordinator,
            Arg.Any<CancellationToken>()).Returns(false);

        var act = async () => await _sut.CoordinatorEndorseAsync(reportId, nonCoordinator, null, Xunit.TestContext.Current.CancellationToken);
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

        await _sut.CoordinatorEndorseAsync(reportId, coordinator, null, Xunit.TestContext.Current.CancellationToken);

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

        var result = await _sut.CoordinatorEndorseWithResultAsync(reportId, coordinator, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(category);
        _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId!.Value, nonCoordinator,
            Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CoordinatorEndorseWithResultAsync(reportId, nonCoordinator, null, Xunit.TestContext.Current.CancellationToken);

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

        var ok = await _sut.CoordinatorRejectAsync(reportId, coordinator, "Missing invoice", Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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

        var result = await _sut.CoordinatorRejectWithResultAsync(reportId, coordinator, "Missing invoice", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(category);
        _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId!.Value, nonCoordinator,
            Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CoordinatorRejectWithResultAsync(reportId, nonCoordinator, "Missing invoice", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a coordinator");
    }

    [HumansFact]
    public async Task CoordinatorEndorseAsync_PersistsTheAuthorizedMaximum()
    {
        var (_, category) = SetupActiveYear();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);

        await _sut.CoordinatorEndorseAsync(reportId, coordinator, 40m, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.MaxAmount.Should().Be(40m);
    }

    [HumansFact]
    public async Task ApproveAsync_MaxAmountOverridesTheCoordinatorsCap()
    {
        var (_, category) = SetupActiveYear();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);
        await _sut.CoordinatorEndorseAsync(reportId, coordinator, 40m, Xunit.TestContext.Current.CancellationToken);

        await _sut.ApproveAsync(reportId, Guid.NewGuid(), null, 25m, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.MaxAmount.Should().Be(25m);
    }

    [HumansFact]
    public async Task ApproveAsync_WithABlankMaxAmount_ClearsTheCoordinatorsCap()
    {
        var (_, category) = SetupActiveYear();
        var coordinator = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);
        SetupCoordinatorAuthz(category.Id, category.TeamId!.Value, coordinator);
        await _sut.CoordinatorEndorseAsync(reportId, coordinator, 40m, Xunit.TestContext.Current.CancellationToken);

        await _sut.ApproveAsync(reportId, Guid.NewGuid(), null, null, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.MaxAmount.Should().BeNull();
    }

    [HumansFact]
    public async Task ApproveAsync_FlipsToApproved_AndAudits()
    {
        var (_, category) = SetupActiveYear();
        var actor = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithStatus(reportId, Guid.NewGuid(), category.Id, Guid.NewGuid(),
            ExpenseReportStatus.Submitted);

        var ok = await _sut.ApproveAsync(reportId, actor, null, null, Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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

        await _sut.ApproveAsync(reportId, actor, overrideCatId, null, Xunit.TestContext.Current.CancellationToken);

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

        var result = await _sut.ApproveWithResultAsync(reportId, actor, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
    }

    [HumansFact]
    public async Task ApproveWithResultAsync_ReturnsFailure_WhenReportMissing()
    {
        var result = await _sut.ApproveWithResultAsync(Guid.NewGuid(), Guid.NewGuid(), null, null, Xunit.TestContext.Current.CancellationToken);

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

        var ok = await _sut.FinanceRejectAsync(reportId, actor, "Wrong category", Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
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

        var result = await _sut.FinanceRejectWithResultAsync(reportId, actor, "Wrong category", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task FinanceRejectWithResultAsync_ReturnsFailure_WhenReportMissing()
    {
        var result = await _sut.FinanceRejectWithResultAsync(Guid.NewGuid(), Guid.NewGuid(), "Wrong category", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Could not reject");
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_ReturnsNonDraftNonWithdrawn_ForFinanceAdmin()
    {
        var (_, category) = SetupActiveYear();
        var yearId = Guid.NewGuid();
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Draft);
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Submitted);
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Approved);
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId, ExpenseReportStatus.Withdrawn);

        var queue = await _sut.GetReviewQueueAsync(Guid.NewGuid(), isFinanceAdmin: true,
            Xunit.TestContext.Current.CancellationToken);
        queue.Should().HaveCount(2);
        queue.Should().OnlyContain(r =>
            r.Status != ExpenseReportStatus.Draft && r.Status != ExpenseReportStatus.Withdrawn);
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_ScopesToOwnReportsAndCoordinatedCategories()
    {
        // The one queue replaced a separate coordinator page (peterdrier/Humans#1447): a
        // coordinator sees their own reports plus their departments', and nothing else.
        var (_, category) = SetupActiveYear();
        var coordinatorUserId = Guid.NewGuid();
        var yearId = Guid.NewGuid();

        var ownId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var strangersId = Guid.NewGuid();
        await SeedReportWithStatus(ownId, coordinatorUserId, Guid.NewGuid(), yearId,
            ExpenseReportStatus.Approved);
        await SeedReportWithStatus(departmentId, Guid.NewGuid(), category.Id, yearId,
            ExpenseReportStatus.Submitted);
        await SeedReportWithStatus(strangersId, Guid.NewGuid(), Guid.NewGuid(), yearId,
            ExpenseReportStatus.Submitted);

        _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinatorUserId,
            Arg.Any<CancellationToken>()).Returns([category.TeamId!.Value]);

        var queue = await _sut.GetReviewQueueAsync(coordinatorUserId, isFinanceAdmin: false,
            Xunit.TestContext.Current.CancellationToken);

        queue.Select(r => r.Id).Should().BeEquivalentTo([ownId, departmentId]);
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_ShowsPlainMemberOnlyTheirOwnReports()
    {
        var (_, category) = SetupActiveYear();
        var memberUserId = Guid.NewGuid();
        var yearId = Guid.NewGuid();

        var ownId = Guid.NewGuid();
        await SeedReportWithStatus(ownId, memberUserId, category.Id, yearId,
            ExpenseReportStatus.Submitted);
        await SeedReportWithStatus(Guid.NewGuid(), Guid.NewGuid(), category.Id, yearId,
            ExpenseReportStatus.Submitted);

        _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(memberUserId,
            Arg.Any<CancellationToken>()).Returns([]);

        var queue = await _sut.GetReviewQueueAsync(memberUserId, isFinanceAdmin: false,
            Xunit.TestContext.Current.CancellationToken);

        queue.Select(r => r.Id).Should().BeEquivalentTo([ownId]);
    }

    [HumansFact]
    public async Task GetCoordinatorQueueAsync_ReturnsEmptyWhenNoTeams()
    {
        _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(Arg.Any<Guid>(),
            Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetCoordinatorQueueAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
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

        var result = await _sut.GetCoordinatorQueueAsync(coordinatorUserId, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(submittedId);
    }

    // ─────────────────────── Holded timeline (submitter view) ───────────────────

    [HumansFact]
    public async Task GetHoldedTimelineAsync_builds_timeline_with_owed_and_other()
    {
        var userId = Guid.NewGuid();
        var (_, category) = SetupActiveYear();
        SetupUserAndProfile(userId, "Alice Tester", "ES9121000418450200051332");
        var reportId = await SeedApprovedReportWithAttachmentAsync(userId, category.Id);
        await _expenseRepo.SetHoldedContactLinkAsync(reportId, "c1", 40000007, FakeNow, Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetHoldedDocIdAsync(reportId, "doc-1", FakeNow, Xunit.TestContext.Current.CancellationToken);

        _holdedFinance.GetCreditorStatusAsync(40000007, Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorStatus(40000007, Balance: -200m, OwedToMember: 200m,
                LastPaymentDate: null, TotalPaid: 0m));

        var report = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        var timeline = await _sut.GetHoldedTimelineAsync(report!, Xunit.TestContext.Current.CancellationToken);

        timeline.Should().NotBeNull();
        timeline.RegisteredInHolded.Should().BeTrue();
        timeline.OwedToMember.Should().Be(200m);
        timeline.OtherAmount.Should().Be(200m - report!.Total);
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_RegisteredTotal_UsesThePayableNotTheReceiptsTotal()
    {
        // The seeded report has a 50 € line capped at 30 €, so a 30 € creditor balance is fully
        // explained by this report — counting the receipts total would leave 20 € as "other".
        var userId = Guid.NewGuid();
        var (_, category) = SetupActiveYear();
        SetupUserAndProfile(userId, "Alice Tester", "ES9121000418450200051332");
        var reportId = await SeedApprovedReportWithAttachmentAsync(userId, category.Id, maxAmount: 30m);
        await _expenseRepo.SetHoldedContactLinkAsync(reportId, "c1", 40000007, FakeNow, Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetHoldedDocIdAsync(reportId, "doc-1", FakeNow, Xunit.TestContext.Current.CancellationToken);

        _holdedFinance.GetCreditorStatusAsync(40000007, Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorStatus(40000007, Balance: -30m, OwedToMember: 30m,
                LastPaymentDate: null, TotalPaid: 0m));

        var report = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        var timeline = await _sut.GetHoldedTimelineAsync(report!, Xunit.TestContext.Current.CancellationToken);

        report!.Total.Should().Be(50m);
        timeline!.MemberRegisteredTotal.Should().Be(30m);
        timeline.OtherAmount.Should().Be(0m);
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_CarriesPaidTotalAndDate()
    {
        var userId = Guid.NewGuid();
        var (_, category) = SetupActiveYear();
        SetupUserAndProfile(userId, "Alice Tester", "ES9121000418450200051332");
        var reportId = await SeedApprovedReportWithAttachmentAsync(userId, category.Id);
        await _expenseRepo.SetHoldedContactLinkAsync(reportId, "c1", 40000007, FakeNow, Xunit.TestContext.Current.CancellationToken);
        await _expenseRepo.SetHoldedDocIdAsync(reportId, "doc-1", FakeNow, Xunit.TestContext.Current.CancellationToken);

        _holdedFinance.GetCreditorStatusAsync(40000007, Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorStatus(40000007, Balance: -50m, OwedToMember: 50m,
                LastPaymentDate: new LocalDate(2026, 4, 20), TotalPaid: 50m));

        var report = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        var timeline = await _sut.GetHoldedTimelineAsync(report!, Xunit.TestContext.Current.CancellationToken);

        timeline!.TotalPaid.Should().Be(50m);
        timeline.PaidOn.Should().Be(new LocalDate(2026, 4, 20));
    }

    // ─────────────────────── Travel wizard methods ────────────────────────────

    [HumansFact]
    public async Task AddMileageLineWithResultAsync_ComputesAmount_FormatsDescription_SetsType()
    {
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(userId, userId, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.AddMileageLineWithResultAsync(id, userId, "Berlin", "Barcelona", 1281m, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        var line = (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Lines.Single();
        line.LineType.Should().Be(ExpenseLineType.Mileage);
        line.Amount.Should().Be(333.06m); // 1281 * 0.26
        line.Description.Should().Be("Berlin to Barcelona, 1281 km @ €0.26 = €333.06");
        line.AttachmentId.Should().BeNull();
    }

    [HumansFact]
    public async Task AddPerDiemLineWithResultAsync_Overnight_ComputesAmount_FormatsDescription_SetsType()
    {
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(userId, userId, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.AddPerDiemLineWithResultAsync(id, userId, PerDiemKind.Overnight, 3, "Assembly Madrid", Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        var line = (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Lines.Single();
        line.LineType.Should().Be(ExpenseLineType.PerDiem);
        line.Amount.Should().Be(160.02m); // 3 * 53.34
        line.Description.Should().Be("Per diem: 3 days overnight @ €53.34 = €160.02 — Assembly Madrid");
        line.AttachmentId.Should().BeNull();
    }

    [HumansFact]
    public async Task AddPerDiemLineWithResultAsync_DayTrip_SingleDay_UsesSingularAndDayTripRate()
    {
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(userId, userId, category.Id, null, Xunit.TestContext.Current.CancellationToken);

        await _sut.AddPerDiemLineWithResultAsync(id, userId, PerDiemKind.DayTrip, 1, null, Xunit.TestContext.Current.CancellationToken);

        var line = (await _sut.GetAsync(id, Xunit.TestContext.Current.CancellationToken))!.Lines.Single();
        line.Amount.Should().Be(26.67m);
        line.Description.Should().Be("Per diem: 1 day day-trip @ €26.67 = €26.67");
    }

    // ─────────────────────── Holded contact enrichment ───────────────────────

    [HumansFact]
    public async Task DrainHoldedOutboxAsync_SecondReport_SeedsContactIdFromTheMembersEarlierReport()
    {
        // A member whose Holded contact predates holded_creditor_contacts has a contact id on older
        // reports and no binding row. Seeding only from the report being pushed sends Finance a null,
        // which POSTs a second contact and splits their payables — so seed from the earlier report.
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(userId, UserFixtures.Profile(
                burnerName: "Meri",
                firstName: "Maria",
                lastName: "Garcia",
                iban: "ES9121000418450200051332")));
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(
            MakeCategorySnapshot(category.Id, teamId: null, "Test Category"));
        _holdedFinance.EnsureCreditorContactAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("contact-123");
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");
        _holdedClient.GetContactAsync("contact-123", Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "contact-123", SupplierAccountNum = 40000007 });
        _holdedClient.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldedPurchaseDocumentDto
            {
                Id = "doc-1",
                DocNumber = "",
                Subtotal = 0m,
                Tax = 0m,
                Total = 0m,
                PaymentsTotal = 0m,
                PaymentsPending = 0m,
            });
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });

        // First report links the member to contact-123 — this is the pre-existing Holded contact.
        await SeedApprovedReportWithAttachmentAsync(userId, category.Id);
        await _sut.DrainHoldedOutboxAsync(100, Xunit.TestContext.Current.CancellationToken);
        _holdedFinance.ClearReceivedCalls();

        // Act — a fresh report for the same member carries no contact id of its own.
        await SeedApprovedReportWithAttachmentAsync(userId, category.Id);
        await _sut.DrainHoldedOutboxAsync(100, Xunit.TestContext.Current.CancellationToken);

        // Assert — the earlier report's contact id (and its account number) are passed as the seed,
        // so Finance updates the existing contact instead of creating a duplicate.
        await _holdedFinance.Received(1).EnsureCreditorContactAsync(
            userId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            "contact-123", 40000007, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DrainHoldedOutboxAsync_DelegatesContactEnrichmentToFinance_PersistsContactLink()
    {
        // Arrange — active year + user with distinct legal name and burner
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        const string legalFirst = "Maria";
        const string legalLast = "Garcia";
        const string legalName = "Maria Garcia";
        const string burnerName = "Meri"; // deliberately different from legal name
        const string iban = "ES9121000418450200051332";

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(userId, UserFixtures.Profile(
                burnerName: burnerName,
                firstName: legalFirst,
                lastName: legalLast,
                iban: iban)));

        // Seed approved report with an attachment via the real service flow
        var reportId = await SeedApprovedReportWithAttachmentAsync(userId, category.Id);

        // Reload so we can verify the line's attachment key for fileStorage
        var reportBefore = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        var line = reportBefore!.Lines[0];

        // Configure Holded substitutes — contact enrichment is delegated to Finance now.
        _holdedFinance.EnsureCreditorContactAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("contact-123");
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");
        _holdedClient.GetContactAsync("contact-123", Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "contact-123", SupplierAccountNum = 40000007 });
        _holdedClient.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldedPurchaseDocumentDto
            {
                Id = "doc-1",
                DocNumber = "",
                Subtotal = 0m,
                Tax = 0m,
                Total = 0m,
                PaymentsTotal = 0m,
                PaymentsPending = 0m,
            });
        _fileStorage.TryReadAsync(
                ExpenseReportService.AttachmentKey(line.Attachment!.Id, line.Attachment.Extension),
                Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });

        // Also set up category for DrainHoldedOutboxAsync (it re-fetches)
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(
            MakeCategorySnapshot(category.Id, teamId: null, "Test Category"));

        // Act
        await _sut.DrainHoldedOutboxAsync(100, Xunit.TestContext.Current.CancellationToken);

        // Assert — contact enrichment delegated to Finance with the legal name, burner and iban
        // (the contact payload itself — Name/TradeName/CustomId — is covered in HoldedFinanceServiceTests).
        await _holdedFinance.Received(1).EnsureCreditorContactAsync(
            userId, legalName, burnerName, iban,
            Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());

        // Assert — the resolved 400000xx number is written back to the member's binding
        await _holdedFinance.Received(1).SetCreditorAccountNumAsync(
            userId, 40000007, Arg.Any<CancellationToken>());

        // Assert — contact link mirrored onto the report
        var loaded = await _sut.GetAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        loaded!.HoldedContactId.Should().Be("contact-123");
        loaded.HoldedSupplierAccountNum.Should().Be(40000007);
    }

    /// <summary>
    /// Seeds a report all the way through Draft → line → attachment → Submit → Approve
    /// using the real sut + expenseRepo, so the outbox event row is written.
    /// </summary>
    private async Task<Guid> SeedApprovedReportWithAttachmentAsync(
        Guid submitterId, Guid categoryId, decimal? maxAmount = null)
    {
        var reportId = await _sut.CreateDraftAsync(submitterId, submitterId, categoryId, "outbox test note", Xunit.TestContext.Current.CancellationToken);
        var lineId = await _sut.AddLineAsync(reportId, submitterId, false, "Test line", 50m, ct: Xunit.TestContext.Current.CancellationToken);

        await using var stream = new MemoryStream([7, 8, 9]);
        await _sut.AttachFileToLineAsync(
            reportId, submitterId, false, lineId, "receipt.pdf", "application/pdf", stream, Xunit.TestContext.Current.CancellationToken);

        var submitted = await _sut.SubmitAsync(reportId, submitterId, false, Xunit.TestContext.Current.CancellationToken);
        if (!submitted) throw new InvalidOperationException("SeedApprovedReportWithAttachmentAsync: SubmitAsync returned false");

        var approved = await _sut.ApproveAsync(reportId, Guid.NewGuid(), null, maxAmount, Xunit.TestContext.Current.CancellationToken);
        if (!approved) throw new InvalidOperationException("SeedApprovedReportWithAttachmentAsync: ApproveAsync returned false");

        return reportId;
    }

    // ─────────────────────────── Helpers ─────────────────────────────────────

    /// <summary>
    /// Stubs the active budget year and one category on <c>IBudgetServiceRead</c>. Builds the
    /// contract DTOs directly: Budget's entities are internal to <c>Humans.Budget</c> since its
    /// G5 move (nobodies-collective/Humans#866), and the DTOs are all this test ever asserted on.
    /// </summary>
    private (BudgetYearDetail Year, BudgetCategorySnapshot Category) SetupActiveYear()
    {
        var teamId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var yearId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        var yearDetail = new BudgetYearDetail(
            yearId,
            "2026",
            "Test Year 2026",
            BudgetYearStatus.Active,
            false,
            [
                new BudgetGroupDetail(
                    groupId,
                    yearId,
                    "Test Group",
                    0,
                    false,
                    false,
                    false,
                    null,
                    [
                        new BudgetCategoryDetail(
                            categoryId,
                            groupId,
                            "Test Category",
                            0m,
                            ExpenditureType.CapEx,
                            teamId,
                            0,
                            [])
                    ])
            ]);

        var category = MakeCategorySnapshot(categoryId, teamId, "Test Category", groupId, yearId);

        _budgetService.GetActiveYearAsync().Returns(yearDetail);
        _budgetService.GetYearByIdAsync(yearId).Returns(yearDetail);
        _budgetService.GetCategoryByIdAsync(categoryId).Returns(category);

        return (yearDetail, category);
    }

    /// <summary>
    /// A non-null <paramref name="budgetYearId"/> fills in the group/year chain, which is how the
    /// service checks that a pending report's new category belongs to that report's own year.
    /// </summary>
    private static BudgetCategorySnapshot MakeCategorySnapshot(
        Guid id, Guid? teamId, string name = "Cat", Guid? groupId = null, Guid? budgetYearId = null)
    {
        var group = groupId ?? Guid.NewGuid();
        return new(
            id,
            group,
            name,
            0m,
            ExpenditureType.CapEx,
            teamId,
            0,
            budgetYearId is { } yearId
                ? new BudgetCategoryGroupSnapshot(group, yearId, "Test Group", false, false, null)
                : null,
            []);
    }

    private void SetupUserAndProfile(Guid userId, string displayName, string iban)
    {
        var nameParts = displayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var firstName = nameParts.Length > 0 ? nameParts[0] : displayName;
        var lastName = nameParts.Length > 1 ? nameParts[1] : "Tester";

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(userId, UserFixtures.Profile(
                burnerName: displayName,
                firstName: firstName,
                lastName: lastName,
                iban: iban)));
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
        _budgetService.GetCategoryByIdAsync(categoryId)
            .Returns(MakeCategorySnapshot(categoryId, teamId));
        _teamService.IsUserCoordinatorOfTeamAsync(teamId, coordinatorUserId,
            Arg.Any<CancellationToken>()).Returns(true);
    }

    private async Task SeedReportWithStatus(
        Guid reportId, Guid submitter, Guid categoryId, Guid yearId,
        ExpenseReportStatus status, string payeeIban = "")
    {
        var now = Instant.FromUtc(2026, 5, 1, 0, 0);
        var report = new ExpenseReport
        {
            Id = reportId,
            SubmitterUserId = submitter,
            BudgetCategoryId = categoryId,
            BudgetYearId = yearId,
            Status = status,
            PayeeIban = payeeIban,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var ctx = new ExpensesDbContext(_expensesOptions);
        ctx.ExpenseReports.Add(report);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
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
