using Xunit;
using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Expenses.Data;
using Humans.Teams.Contracts;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Email.Contracts;
using Humans.Expenses.Tests.Infrastructure;

namespace Humans.Expenses.Tests.Services;

// The drain gate is static (one drain per process), so classes that drain run serially.
[Collection("HoldedDrainGate")]
public class ExpenseReportServiceHoldedOutboxTests
{
    private readonly IStringLocalizer<ExpensesResource> _localizer =
        new StringLocalizer<ExpensesResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    private const int BatchSize = 100;

    private readonly IExpenseRepository _repo;
    private readonly IBudgetService _budgetService;
    private readonly IUserService _userService;
    private readonly IHoldedService _holdedService = Substitute.For<IHoldedService>();
    private readonly IHoldedClient _holdedClient;
    private readonly IFinanceService _finance = Substitute.For<IFinanceService>();
    private readonly IHoldedFinanceService _holdedFinance;
    private readonly IHoldedFinanceWrite _holdedFinanceWrite;
    private readonly IFileStorage _fileStorage;
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock;
    private readonly ExpenseReportService _sut;
    private readonly ExpenseReportBackgroundProcessor _backgroundProcessor;

    private static readonly Instant Now = Instant.FromUtc(2026, 5, 10, 12, 0);
    private static readonly Guid SubmitterId = Guid.NewGuid();
    private static readonly Guid CategoryId = Guid.NewGuid();

    private readonly BudgetCategorySnapshot _category;

    public ExpenseReportServiceHoldedOutboxTests()
    {
        var groupId = Guid.NewGuid();
        _category = new BudgetCategorySnapshot(
            CategoryId,
            groupId,
            "Camp",
            0m,
            default,
            null,
            0,
            new BudgetCategoryGroupSnapshot(groupId, Guid.NewGuid(), "Camp Build", false, false, null),
            []);

        _repo = Substitute.For<IExpenseRepository>();
        _budgetService = Substitute.For<IBudgetService>();
        _userService = Substitute.For<IUserService>();
        _holdedClient = Substitute.For<IHoldedClient>();
        // DrainHoldedOutboxAsync returns early unless IHoldedClient.IsConfigured, so every test
        // that expects a push has to say the client is configured.
        _holdedClient.IsConfigured.Returns(true);
        _holdedService.IsConfigured.Returns(true);
        _fileStorage = Substitute.For<IFileStorage>();
        _clock = new FakeClock(Now);

        // The doc-id write is conditional; it succeeds unless a test says another push got there first.
        _repo.SetLineHoldedDocIdAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _budgetService.GetCategoryByIdAsync(CategoryId)
            .Returns(_category);

        var submitter = new User { Id = SubmitterId, DisplayName = "Alice Smith" };
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [SubmitterId] = MinimalUserInfo(submitter) }));

        // Contact enrichment is Finance's: ExpenseReportService delegates to
        // EnsureCreditorContactAsync for the contact id, then resolves its supplier-account number.
        _holdedFinance = Substitute.For<IHoldedFinanceService>();
        _holdedFinanceWrite = Substitute.For<IHoldedFinanceWrite>();
        _holdedFinanceWrite.EnsureCreditorContactAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("holded-contact-1");
        _holdedClient.GetContactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "holded-contact-1", SupplierAccountNum = 40000050 });

        // Step 4 (approve) reads the doc back first to check ApprovedAt — not yet approved by
        // default, so the happy-path tests that don't care about approval still exercise it.
        _holdedClient.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakePurchaseDoc());

        _sut = new ExpenseReportService(
            _repo,
            _fileStorage,
            _budgetService,
            Substitute.For<ITeamService>(),
            _userService,
            Substitute.For<IUserEmailService>(),
            Substitute.For<IEmailService>(),
            TestExpensesEmails.Create(),
            _auditLog,
            _holdedService,
            _holdedFinance,
            _finance,
            Substitute.For<IFinanceWrite>(),
            Substitute.For<Humans.Preapprovals.Contracts.IPreapprovalService>(),
            Substitute.For<Humans.Preapprovals.Contracts.IPreapprovalWrite>(),
            _clock,
            Substitute.For<ILogger<ExpenseReportService>>(),
            Options.Create(new TravelReimbursementConfig()));
        _backgroundProcessor = new ExpenseReportBackgroundProcessor(
            _repo, _fileStorage, _userService, _holdedClient, _holdedFinance, _holdedFinanceWrite, _auditLog, _clock,
            NullLogger<ExpenseReportBackgroundProcessor>.Instance);
    }

    [HumansFact]
    public async Task GetHoldedBookingsAsync_lists_linked_lines_with_the_values_the_push_wrote()
    {
        // The same description, notes and booked total ExpenseHoldedPublisher writes; a trimmed
        // line reports what it booked, an unlinked or declined line is absent, a legacy
        // report-level doc is not a line booking.
        var report = MakeReport() with { Total = 30m, MaxAmount = 25m, HoldedContactId = "c-1" };
        report = report with
        {
            Lines =
            [
                report.Lines[0] with { Amount = 20m, SortOrder = 0, HoldedDocId = "doc-a" },
                report.Lines[0] with { Id = Guid.NewGuid(), Description = "Nails", Amount = 10m, SortOrder = 1, HoldedDocId = "doc-b" },
                report.Lines[0] with { Id = Guid.NewGuid(), Description = "Declined", Amount = 5m, SortOrder = 2, HoldedDocId = null, Declined = true },
            ],
        };
        var legacy = MakeReport("doc-legacy");
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([report, legacy]);

        var bookings = await new ExpenseHoldedBookingService(_repo, _userService, _holdedService).GetHoldedBookingsAsync(TestContext.Current.CancellationToken);

        bookings.Should().HaveCount(2);
        bookings[0].Should().Be(new ExpenseHoldedBooking(
            report.Id, report.Lines[0].Id, SubmitterId, "c-1", "doc-a", "ER: Wood", $"Expense report {report.Id}", 20m));
        bookings[1].HoldedDocId.Should().Be("doc-b");
        bookings[1].BookedAmount.Should().Be(5m);
    }

    [HumansFact]
    public async Task GetHoldedBookingsAsync_SubmitterMergedAway_NamesTheSurvivor()
    {
        // The creditor binding sits on the survivor, so the booking has to name them too.
        var survivor = new User { Id = Guid.NewGuid(), DisplayName = "Alice Smith" };
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [SubmitterId] = MinimalUserInfo(survivor) with { MergedUserIds = [SubmitterId] } }));
        var report = MakeReport();
        report = report with { Lines = [report.Lines[0] with { HoldedDocId = "doc-a" }] };
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([report]);

        var bookings = await new ExpenseHoldedBookingService(_repo, _userService, _holdedService).GetHoldedBookingsAsync(TestContext.Current.CancellationToken);

        bookings.Should().ContainSingle().Which.CreditorUserId.Should().Be(survivor.Id);
    }

    [HumansFact]
    public async Task GetHoldedReconLinesAsync_ApprovedLinesPushedOrNot_SkippedAndDeclinedAbsent()
    {
        // Cap 25 over 20 + 10 + 7: the second line is trimmed to 5, the third books nothing and gets no document.
        var report = MakeReport() with { Total = 37m, MaxAmount = 25m, HoldedContactId = "c-1" };
        report = report with
        {
            Lines =
            [
                report.Lines[0] with { Amount = 20m, SortOrder = 0, HoldedDocId = "doc-a" },
                report.Lines[0] with { Id = Guid.NewGuid(), Description = "Nails", Amount = 10m, SortOrder = 1, HoldedDocId = null },
                report.Lines[0] with { Id = Guid.NewGuid(), Description = "Skipped", Amount = 7m, SortOrder = 2, HoldedDocId = null },
                report.Lines[0] with { Id = Guid.NewGuid(), Description = "Declined", Amount = 5m, SortOrder = 3, Declined = true },
            ],
        };
        var failed = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        failed.FailedPermanently = true;
        failed.ProcessedAt = Now;
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([report]);
        _repo.GetLatestOutboxByReportAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, HoldedExpenseOutboxEvent> { [report.Id] = failed });

        var lines = await new ExpenseHoldedBookingService(_repo, _userService, _holdedService)
            .GetHoldedReconLinesAsync(TestContext.Current.CancellationToken);

        lines.Should().HaveCount(2);
        lines[0].Should().Be(new ExpenseHoldedReconLine(
            report.Id, report.Lines[0].Id, ExpenseReportStatus.Approved, SubmitterId, "c-1", "doc-a", "ER: Wood",
            $"Expense report {report.Id}", 20m, report.SubmittedAt!.Value, ExpenseHoldedSyncState.Failed, Now));
        lines[1].HoldedDocId.Should().BeNull();
        lines[1].BookedAmount.Should().Be(5m);
        await _repo.DidNotReceive().GetLatestOutboxForReportAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetHoldedReconLinesAsync_OtherStatusOnlyLinkedLines_LegacyIsOneReportLevelEntry()
    {
        var withdrawn = MakeReport() with { Status = ExpenseReportStatus.Withdrawn };
        withdrawn = withdrawn with
        {
            Lines =
            [
                withdrawn.Lines[0] with { HoldedDocId = "doc-w" },
                withdrawn.Lines[0] with { Id = Guid.NewGuid(), SortOrder = 1, HoldedDocId = null },
            ],
            Total = 100m,
        };
        var submitted = MakeReport() with { Status = ExpenseReportStatus.Submitted };
        var legacy = MakeReport("doc-legacy");
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([withdrawn, submitted, legacy]);
        _repo.GetLatestOutboxByReportAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, HoldedExpenseOutboxEvent>());

        var lines = await new ExpenseHoldedBookingService(_repo, _userService, _holdedService)
            .GetHoldedReconLinesAsync(TestContext.Current.CancellationToken);

        lines.Should().HaveCount(2);
        lines[0].HoldedDocId.Should().Be("doc-w");
        lines[0].ReportStatus.Should().Be(ExpenseReportStatus.Withdrawn);
        lines[1].Should().Match<ExpenseHoldedReconLine>(l =>
            l.LineId == null && l.HoldedDocId == "doc-legacy" && l.BookedAmount == 50m
            && l.SyncState == ExpenseHoldedSyncState.NotQueued);
    }

    /// <summary>One bookable line ("Wood", €50) — the minimum a real approved report carries, so
    /// the per-line push has a doc to create. Tests that need other shapes override Lines.</summary>
    private static ExpenseReportDto MakeReport(string? holdedDocId = null)
    {
        var reportId = Guid.NewGuid();
        return new()
        {
            Id = reportId,
            SubmitterUserId = SubmitterId,
            HoldedAccountNumber = 0,
            BudgetCategoryId = CategoryId,
            BudgetYearId = Guid.NewGuid(),
            Status = ExpenseReportStatus.Approved,
            PayeeName = "Alice Smith",
            PayeeIban = "",
            Total = 50m,
            SubmittedAt = Instant.FromUtc(2026, 5, 1, 10, 0),
            CreatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
            Note = "Team expenses",
            HoldedDocId = holdedDocId,
            Lines = new List<ExpenseLineDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ExpenseReportId = reportId,
                    Description = "Wood",
                    Amount = 50m,
                    LineType = ExpenseLineType.Receipt,
                    SortOrder = 1,
                },
            },
        };
    }

    /// <summary>A two-line report whose attachments carry the given push stamps — the shape the
    /// attachment-idempotency tests need.</summary>
    private static ExpenseReportDto MakeReportWithAttachments(
        string? holdedDocId, Instant? alreadyPushed, Instant? notYetPushed)
    {
        var report = MakeReport(holdedDocId);
        return report with
        {
            Lines =
            [
                MakeLineWithAttachment(report.Id, "first.pdf", 0, alreadyPushed),
                MakeLineWithAttachment(report.Id, "second.pdf", 1, notYetPushed),
            ],
        };
    }

    private static ExpenseLineDto MakeLineWithAttachment(
        Guid reportId, string fileName, int sortOrder, Instant? holdedUploadedAt)
    {
        var attachmentId = Guid.NewGuid();
        return new ExpenseLineDto
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            Description = fileName,
            Amount = 10m,
            LineType = ExpenseLineType.Receipt,
            SortOrder = sortOrder,
            AttachmentId = attachmentId,
            Attachment = new ExpenseAttachmentDto
            {
                Id = attachmentId,
                OriginalFileName = fileName,
                Extension = ".pdf",
                ContentType = "application/pdf",
                SizeBytes = 3,
                UploadedByUserId = SubmitterId,
                UploadedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
                HoldedUploadedAt = holdedUploadedAt,
            },
        };
    }

    // Built directly: the shared UserInfoStubHelpers read through a UsersDbContext this
    // test project cannot see.
    private static UserInfo MinimalUserInfo(User user) =>
        UserInfo.Create(user, [], [], [], profile: null, []);

    private static HoldedPurchaseDocumentDto MakePurchaseDoc(Instant? approvedAt = null) => new()
    {
        ApprovedAt = approvedAt,
    };

    private static HoldedExpenseOutboxEvent MakeEvent(
        Guid reportId, HoldedExpenseOutboxEventType eventType) => new()
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            EventType = eventType,
            OccurredAt = Now,
        };

    [HumansFact]
    public async Task EmptyQueue_NoClientCalls()
    {
        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(null!, Arg.Any<CancellationToken>());
    }

    private ExpenseReportBackgroundProcessor NewProcessor() => new(
        _repo, _fileStorage, _userService, _holdedClient, _holdedFinance, _holdedFinanceWrite, _auditLog, _clock,
        NullLogger<ExpenseReportBackgroundProcessor>.Instance);

    [HumansFact]
    public async Task OverlappingDrain_IsSkipped_AndTheEventIsProcessedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return "holded-doc-1";
            });

        var first = NewProcessor().DrainHoldedOutboxAsync(BatchSize, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // A different processor instance (the type is Scoped) still hits the same gate.
        await NewProcessor().DrainHoldedOutboxAsync(BatchSize, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Only the first drain ever read the queue; the second created nothing.
        await _repo.Received(1).GetUnprocessedOutboxAsync(
            Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>());

        release.SetResult();
        await first;

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NextDrain_RunsNormally_AfterADrainThatThrew()
    {
        // A non-Holded exception escapes the processor; the gate must still be released.
        var ct = TestContext.Current.CancellationToken;
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<string>(new InvalidOperationException("boom")),
                Task.FromResult("holded-doc-1"));

        var act = () => NewProcessor().DrainHoldedOutboxAsync(BatchSize, ct);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await NewProcessor().DrainHoldedOutboxAsync(BatchSize, ct);

        await _holdedClient.Received(2).CreatePurchaseDocumentAsync(
            Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_HappyPath_ClientCalledAndDocIdPersisted()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        const string holdedDocId = "holded-doc-999";

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), 100, Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(holdedDocId);

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        // The report's Note stays in Humans; the doc's description is "ER: " + the line, and the
        // report ref moves to Notes so it doesn't clutter the Holded-rendered ledger description.
        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                string.Equals(i.ContactName, "Alice Smith", StringComparison.Ordinal) &&
                string.Equals(i.Description, "ER: Wood", StringComparison.Ordinal) &&
                string.Equals(i.Notes, $"Expense report {report.Id}", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        await _repo.Received(1).SetLineHoldedDocIdAsync(
            report.Lines[0].Id, holdedDocId, Now, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_ExcludesProofRows_FromDocLinesAndAttachmentUploads()
    {
        // Proof rows back an invoice line for review only — Holded gets the invoice line and the
        // invoice file, never the proofs.
        var invoiceLine = MakeLineWithAttachment(Guid.NewGuid(), "invoice.pdf", 0, holdedUploadedAt: null)
            with
        { LineType = ExpenseLineType.Invoice, Amount = 1000m };
        var proofLine = MakeLineWithAttachment(Guid.NewGuid(), "proof.pdf", 1, holdedUploadedAt: null)
            with
        { ParentLineId = invoiceLine.Id, Amount = 400m };

        var report = MakeReport() with { Total = 1000m, Lines = [invoiceLine, proofLine] };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-invoice-1");
        _fileStorage.TryReadAsync(
                $"uploads/expense-attachments/{invoiceLine.Attachment!.Id}.pdf",
                Arg.Any<CancellationToken>())
            .Returns([1, 2]);

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.Lines.Count == 1 && i.Lines[0].Amount == 1000m),
            Arg.Any<CancellationToken>());
        await _holdedClient.Received(1).UploadAttachmentAsync(
            "doc-invoice-1",
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "invoice.pdf"),
            Arg.Any<CancellationToken>());
        await _holdedClient.DidNotReceive().UploadAttachmentAsync(
            Arg.Any<string>(),
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "proof.pdf"),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_BooksLinesToTheMappedHoldedAccount()
    {
        // Tags are dead on the write side — the doc is booked to the right
        // department directly via items[].account, resolved from the category's active mapping.
        var report = MakeReport() with
        {
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Wood", Amount = 42m, LineType = ExpenseLineType.Receipt, SortOrder = 1 },
            },
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedFinance.GetHoldedAccountIdForCategoryAsync(CategoryId, Arg.Any<CancellationToken>())
            .Returns("holded-acc-629001");
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.Lines.Count == 1 &&
                string.Equals(i.Lines[0].AccountId, "holded-acc-629001", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_BooksLinesToTheAccountChosenOnTheReport()
    {
        // A report that names its own account books there directly; the category map is only
        // the fallback for rows that predate the account column.
        var report = MakeReport() with
        {
            HoldedAccountNumber = 62900900,
            HoldedAccountId = "holded-acc-629009",
            BudgetCategoryId = null,
            BudgetYearId = null,
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Wood", Amount = 42m, LineType = ExpenseLineType.Receipt, SortOrder = 1 },
            },
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.Lines.Count == 1 &&
                string.Equals(i.Lines[0].AccountId, "holded-acc-629009", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _holdedFinance.DidNotReceive().GetHoldedAccountIdForCategoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_UnmappedCategory_LinesHaveNullAccountId()
    {
        var report = MakeReport() with
        {
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Wood", Amount = 42m, LineType = ExpenseLineType.Receipt, SortOrder = 1 },
            },
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedFinance.GetHoldedAccountIdForCategoryAsync(CategoryId, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i => i.Lines[0].AccountId == null),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_CappedReport_AppendsNegativeAdjustmentLine()
    {
        // The receipts book in full and a negative line brings the doc down to the authorized cap,
        // so what Holded owes the member matches what was approved.
        var report = MakeReport() with
        {
            Total = 100m,
            MaxAmount = 60m,
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Wood", Amount = 100m, LineType = ExpenseLineType.Receipt, SortOrder = 1 },
            },
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedFinance.GetHoldedAccountIdForCategoryAsync(CategoryId, Arg.Any<CancellationToken>())
            .Returns("holded-acc-629001");
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-capped");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.Lines.Count == 2 &&
                i.Lines[1].Amount == -40m &&
                string.Equals(i.Lines[1].Description, "Authorized maximum €60.00 — adjustment", StringComparison.Ordinal) &&
                string.Equals(i.Lines[1].AccountId, "holded-acc-629001", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_CapAtOrAboveTheTotal_HasNoAdjustmentLine()
    {
        var report = MakeReport() with
        {
            Total = 100m,
            MaxAmount = 100m,
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Wood", Amount = 100m, LineType = ExpenseLineType.Receipt, SortOrder = 1 },
            },
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-uncapped");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i => i.Lines.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_AttachmentsUploadedInOrder()
    {
        var attachment1 = new ExpenseAttachmentDto
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "receipt1.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
        };
        var attachment2 = new ExpenseAttachmentDto
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "receipt2.jpg",
            Extension = ".jpg",
            ContentType = "image/jpeg",
            SizeBytes = 200,
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
        };

        var report = MakeReport() with
        {
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Line A", Amount = 10m, LineType = ExpenseLineType.Receipt, SortOrder = 1, AttachmentId = attachment1.Id, Attachment = attachment1 },
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Line B", Amount = 20m, LineType = ExpenseLineType.Receipt, SortOrder = 2, AttachmentId = attachment2.Id, Attachment = attachment2 },
            }
        };

        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        // One doc per line — each receipt lands on its own line's doc.
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-line-a", "doc-line-b");

        _fileStorage.TryReadAsync(
                $"uploads/expense-attachments/{attachment1.Id}.pdf",
                Arg.Any<CancellationToken>())
            .Returns([1, 2]);
        _fileStorage.TryReadAsync(
                $"uploads/expense-attachments/{attachment2.Id}.jpg",
                Arg.Any<CancellationToken>())
            .Returns([3, 4]);

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).UploadAttachmentAsync(
            "doc-line-a",
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "receipt1.pdf" && a.ContentType == "application/pdf"),
            Arg.Any<CancellationToken>());

        await _holdedClient.Received(1).UploadAttachmentAsync(
            "doc-line-b",
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "receipt2.jpg" && a.ContentType == "image/jpeg"),
            Arg.Any<CancellationToken>());

        await _repo.Received(1).SetLineHoldedDocIdAsync(
            report.Lines[0].Id, "doc-line-a", Now, Arg.Any<CancellationToken>());
        await _repo.Received(1).SetLineHoldedDocIdAsync(
            report.Lines[1].Id, "doc-line-b", Now, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_LinesWithoutAttachments_SkippedCleanly()
    {
        var report = MakeReport() with
        {
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "No receipt", Amount = 5m, LineType = ExpenseLineType.Receipt, SortOrder = 1, AttachmentId = null, Attachment = null },
            }
        };

        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-no-att");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .UploadAttachmentAsync(null!, null!, Arg.Any<CancellationToken>());
        await _repo.Received(1).SetLineHoldedDocIdAsync(
            report.Lines[0].Id, "doc-no-att", Now, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_AttachmentBytesMissing_ThrowsAndDoesNotMarkProcessed()
    {
        // IFileStorage.TryReadAsync returns null for both missing files and IO errors.
        // Either case must keep the outbox event unprocessed so Hangfire can retry —
        // silently skipping would permanently lose the receipt upload to Holded.
        var attachment = new ExpenseAttachmentDto
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "receipt.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
        };
        var report = MakeReport() with
        {
            Lines = new List<ExpenseLineDto>
            {
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Line A", Amount = 10m, LineType = ExpenseLineType.Receipt, SortOrder = 1, AttachmentId = attachment.Id, Attachment = attachment },
            }
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-missing-bytes");
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);

        var act = async () => await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be read from storage*");

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .UploadAttachmentAsync(null!, null!, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkOutboxProcessedAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_LegacyReportLevelDocId_ResumesOntoItInsteadOfCreatingPerLineDocs()
    {
        // A report pushed before per-line docs carries its single doc id on the report. The
        // resume must NOT call CreatePurchaseDocumentAsync — that would mint per-line payables
        // next to the doc Holded already has.
        var report = MakeReport(holdedDocId: "doc-prev-retry");
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(null!, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .SetLineHoldedDocIdAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_LineDocIdAlreadySet_SkipsCreateForThatLineOnly()
    {
        // Idempotency on re-drain: a line whose doc a previous attempt already issued (and
        // persisted early) is not created again; the lines after it still get theirs.
        var report = MakeReport();
        var pushedLine = report.Lines[0] with { HoldedDocId = "doc-line-a" };
        var pendingLine = pushedLine with
        {
            Id = Guid.NewGuid(),
            Description = "Screws",
            SortOrder = 2,
            HoldedDocId = null,
        };
        report = report with { Total = 100m, Lines = [pushedLine, pendingLine] };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-line-b");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i => i.Lines[0].Description == "Screws"),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SetLineHoldedDocIdAsync(
            pendingLine.Id, "doc-line-b", Now, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SetLineHoldedDocIdAsync(
            pushedLine.Id, Arg.Any<string>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_CapInsideAMultiLineReport_TrimsTheLastDocAndSkipsTheRest()
    {
        // 3×100 with a 250 cap: two full docs, the third trimmed to 50 via a negative adjustment
        // on the same account; with a fourth receipt it would get no doc at all.
        ExpenseLineDto Line(string description, int sortOrder) => new()
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = Guid.NewGuid(),
            Description = description,
            Amount = 100m,
            LineType = ExpenseLineType.Receipt,
            SortOrder = sortOrder,
        };
        var report = MakeReport() with
        {
            Total = 300m,
            MaxAmount = 250m,
            Lines = [Line("A", 1), Line("B", 2), Line("C", 3)],
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedFinance.GetHoldedAccountIdForCategoryAsync(CategoryId, Arg.Any<CancellationToken>())
            .Returns("holded-acc-629001");
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-a", "doc-b", "doc-c");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(3).CreatePurchaseDocumentAsync(
            Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>());
        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.Lines.Count == 1 && i.Lines[0].Description == "A" && i.Lines[0].Amount == 100m),
            Arg.Any<CancellationToken>());
        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.Lines.Count == 2 &&
                i.Lines[0].Description == "C" && i.Lines[0].Amount == 100m &&
                i.Lines[1].Amount == -50m &&
                string.Equals(i.Lines[1].Description, "Authorized maximum €250.00 — adjustment", StringComparison.Ordinal) &&
                string.Equals(i.Lines[1].AccountId, "holded-acc-629001", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        // The trim is named in the audit entry alongside the doc ids.
        await _auditLog.Received(1).LogAsync(
            ExpensesAuditActions.ExpenseHoldedPushed, Arg.Any<string>(), report.Id,
            Arg.Is<string>(s =>
                s.Contains("doc-a, doc-b, doc-c") &&
                s.Contains("'C' booked €50.00 of €100.00")),
            Arg.Any<string>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_ReceiptFullyOverTheCap_GetsNoDocNoUploadAndIsNamedInTheAudit()
    {
        // The cap is spent before the second receipt: Holded holds no record of it at all, so
        // the skip must be loud — named in the audit entry, never silent.
        var line1 = MakeLineWithAttachment(Guid.NewGuid(), "first.pdf", 0, holdedUploadedAt: null)
            with
        { Amount = 100m };
        var line2 = MakeLineWithAttachment(Guid.NewGuid(), "second.pdf", 1, holdedUploadedAt: null)
            with
        { Amount = 100m };
        var report = MakeReport() with
        {
            Total = 200m,
            MaxAmount = 100m,
            Lines = [line1, line2],
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-first");
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>());
        await _holdedClient.DidNotReceive().UploadAttachmentAsync(
            Arg.Any<string>(),
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "second.pdf"),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            ExpensesAuditActions.ExpenseHoldedPushed, Arg.Any<string>(), report.Id,
            Arg.Is<string>(s =>
                s.Contains("doc-first") &&
                s.Contains("'second.pdf' (€100.00) not booked — authorized maximum reached.")),
            Arg.Any<string>());
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CreateIncomingDoc_LongUnicodeSummary_RemainsValidUtf8(int padding)
    {
        var description = new string('a', padding) + string.Concat(Enumerable.Repeat("😀", 249));
        var report = MakeReport() with
        {
            Total = 800m,
            MaxAmount = 1m,
            Lines = Enumerable.Range(0, 8).Select(i =>
                MakeLineWithAttachment(Guid.NewGuid(), $"receipt-{i}.pdf", i, holdedUploadedAt: null)
                    with
                { Description = description, Amount = 100m }).ToList(),
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-emoji");
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        var audit = _auditLog.ReceivedCalls().Single(c =>
            string.Equals(c.GetMethodInfo().Name, nameof(IAuditLogService.LogAsync), StringComparison.Ordinal)
            && Equals(c.GetArguments()[0], ExpensesAuditActions.ExpenseHoldedPushed));
        var summary = (string)audit.GetArguments()[3]!;
        summary.Length.Should().BeLessThanOrEqualTo(3500);
        summary.Should().EndWith("…");
        Action encode = () => new System.Text.UTF8Encoding(false, true).GetBytes(summary);
        encode.Should().NotThrow();
        await _repo.Received(1).MarkOutboxProcessedAsync(outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateIncomingDocTag_NoClientCall_MarkedProcessed()
    {
        // v2 has no tag/doc-update endpoint at all — the event type is kept only so any rows
        // queued from before this change still drain instead of poisoning the outbox.
        var report = MakeReport(holdedDocId: "holded-existing-doc");
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.UpdateIncomingDocTag);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(null!, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false, 0)]
    [InlineData(false, 9)]
    [InlineData(true, 0)]
    public async Task Long_outbox_errors_fit_bookkeeping_and_keep_writeoffs_audited(bool permanent, int retries)
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        outboxEvent.RetryCount = retries;
        var error = new string('e', 1999) + "😀" + new string('e', 2100);
        HoldedApiException failure = permanent
            ? new HoldedPermanentException(error)
            : new HoldedTransientException(error);
        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(failure));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        var writeoff = permanent || retries == 9;
        var write = _repo.ReceivedCalls().Single(c => string.Equals(c.GetMethodInfo().Name,
            writeoff ? nameof(IExpenseRepository.MarkOutboxFailedPermanentlyAsync) : nameof(IExpenseRepository.IncrementOutboxRetryAsync),
            StringComparison.Ordinal));
        var savedError = (string)write.GetArguments()[1]!;
        savedError.Length.Should().BeLessThanOrEqualTo(2000);
        char.IsHighSurrogate(savedError[^1]).Should().BeFalse();
        if (writeoff)
        {
            await _auditLog.Received(1).LogAsync(
                ExpensesAuditActions.ExpenseHoldedFailed, Arg.Any<string>(), report.Id,
                Arg.Is<string>(s => s.Length <= 4000 && s.Contains(savedError, StringComparison.Ordinal)),
                Arg.Any<string>());
        }
    }

    [HumansFact]
    public async Task TransientException_IncrementRetry_NotMarkedProcessed()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HoldedTransientException("timeout")));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _repo.Received(1).IncrementOutboxRetryAsync(
            outboxEvent.Id, "timeout", Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().SetLineHoldedDocIdAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().MarkOutboxProcessedAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkOutboxFailedPermanentlyAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(3, 16)]
    public async Task TransientException_BacksOffExponentially(int retryCount, int expectedMinutes)
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        outboxEvent.RetryCount = retryCount;

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HoldedTransientException("timeout")));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _repo.Received(1).IncrementOutboxRetryAsync(
            outboxEvent.Id, "timeout",
            Now + Duration.FromMinutes(expectedMinutes),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task TransientException_OnTheTenthAttempt_IsWrittenOffRatherThanRetriedForever()
    {
        // Nine failures already recorded; this one spends the budget. Writing it off — rather than
        // letting a RetryCount filter drop it out of the drain — is what puts it on the
        // /Expenses/Review banner instead of stranding it invisibly.
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        outboxEvent.RetryCount = 9;

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HoldedTransientException("timeout")));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id,
            Arg.Is<string>(s => s.Contains("10 attempts") && s.Contains("timeout")),
            Now, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .IncrementOutboxRetryAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            ExpensesAuditActions.ExpenseHoldedFailed, Arg.Any<string>(), report.Id,
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task NoApiKeyConfigured_DrainDoesNotTouchTheQueue()
    {
        // Every call would 401 — a permanent error — so draining would write off the whole queue.
        _holdedClient.IsConfigured.Returns(false);
        _holdedService.IsConfigured.Returns(false);

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _repo.DidNotReceiveWithAnyArgs()
            .GetUnprocessedOutboxAsync(default, 0, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AlreadyUploadedAttachments_AreNotSentAgainOnARerun()
    {
        // A re-run after a partial failure (or a finance-admin re-queue) must not add a second
        // copy of every earlier file to the doc Holded already has.
        var report = MakeReportWithAttachments(
            holdedDocId: "holded-doc-1",
            alreadyPushed: Now - Duration.FromHours(1),
            notYetPushed: null);
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).UploadAttachmentAsync(
            "holded-doc-1",
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "second.pdf"),
            Arg.Any<CancellationToken>());
        await _holdedClient.DidNotReceive().UploadAttachmentAsync(
            Arg.Any<string>(),
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "first.pdf"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EachUpload_IsRecordedSoTheNextRunCanSkipIt()
    {
        var report = MakeReportWithAttachments(
            holdedDocId: "holded-doc-1", alreadyPushed: null, notYetPushed: null);
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _repo.Received(2).MarkAttachmentPushedAsync(
            Arg.Any<Guid>(), Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SuccessfulPush_IsRecordedInTheAuditLog()
    {
        // The outbox columns are not readable outside the database and do not survive row
        // cleanup; the audit entry is what keeps the push on the report's history.
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("holded-doc-42");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            ExpensesAuditActions.ExpenseHoldedPushed, Arg.Any<string>(), report.Id,
            Arg.Is<string>(s => s.Contains("holded-doc-42")), Arg.Any<string>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_ApprovesTheDocAfterAttachments()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("holded-doc-42");
        _holdedClient.GetPurchaseDocumentAsync("holded-doc-42", Arg.Any<CancellationToken>())
            .Returns(MakePurchaseDoc(approvedAt: null));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).ApprovePurchaseDocumentAsync(
            "holded-doc-42", Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_AlreadyApprovedOnReDrain_SkipsApproveAndStillSucceeds()
    {
        // A re-drain can reach a doc a prior attempt already approved (e.g. approve succeeded but
        // a later step in this method failed). Re-approving must not throw the event into
        // permanent failure.
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("holded-doc-42");
        _holdedClient.GetPurchaseDocumentAsync("holded-doc-42", Arg.Any<CancellationToken>())
            .Returns(MakePurchaseDoc(approvedAt: Now - Duration.FromHours(1)));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .ApprovePurchaseDocumentAsync(null!, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsQueued_ForAFreshEvent()
    {
        var report = MakeReport();
        _repo.GetLatestOutboxForReportAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc));

        var timeline = await _sut.GetHoldedTimelineAsync(report, TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.Queued);
        timeline.CanRetry.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsRetrying_WithTheErrorAndNextAttempt()
    {
        var report = MakeReport();
        var ev = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        ev.RetryCount = 3;
        ev.LastError = "timeout";
        ev.NextRetryAt = Now + Duration.FromMinutes(16);
        _repo.GetLatestOutboxForReportAsync(report.Id, Arg.Any<CancellationToken>()).Returns(ev);

        var timeline = await _sut.GetHoldedTimelineAsync(report, TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.Retrying);
        timeline.RetryCount.Should().Be(3);
        timeline.LastError.Should().Be("timeout");
        timeline.NextRetryAt.Should().Be(Now + Duration.FromMinutes(16));
        // A finance admin who has fixed the cause should not have to wait out the backoff.
        timeline.CanRetry.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsFailed_EvenThoughAWriteOffAlsoSetsProcessedAt()
    {
        var report = MakeReport();
        var ev = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        ev.FailedPermanently = true;
        ev.ProcessedAt = Now;
        ev.LastError = "400 Bad Request";
        _repo.GetLatestOutboxForReportAsync(report.Id, Arg.Any<CancellationToken>()).Returns(ev);

        var timeline = await _sut.GetHoldedTimelineAsync(report, TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.Failed);
        timeline.CanRetry.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsNotConfigured_RatherThanQueued_WhenThereIsNoApiKey()
    {
        // Otherwise a PR-preview environment shows "Queued" forever for a queue nothing drains.
        _holdedClient.IsConfigured.Returns(false);
        _holdedService.IsConfigured.Returns(false);
        var report = MakeReport();
        _repo.GetLatestOutboxForReportAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc));

        var timeline = await _sut.GetHoldedTimelineAsync(report, TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.NotConfigured);
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsNotQueued_WhenNoPushWasEverQueued()
    {
        var timeline = await _sut.GetHoldedTimelineAsync(MakeReport(), TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.NotQueued);
        timeline.CanRetry.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetFailedHoldedPushReportIdsAsync_IsFinanceAdminOnly(bool isFinanceAdmin)
    {
        var failed = Guid.NewGuid();
        _repo.GetFailedOutboxReportIdsAsync(Arg.Any<CancellationToken>()).Returns([failed]);

        var ids = await _sut.GetFailedHoldedPushReportIdsAsync(isFinanceAdmin, TestContext.Current.CancellationToken);

        ids.Should().BeEquivalentTo(isFinanceAdmin ? [failed] : Array.Empty<Guid>());
    }

    [HumansFact]
    public async Task RequeueHoldedPushWithResultAsync_RequeuesAndAudits()
    {
        var reportId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        _repo.RequeueOutboxForReportAsync(reportId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.RequeueHoldedPushWithResultAsync(
            reportId, actorId, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        await _auditLog.Received(1).LogAsync(
            ExpensesAuditActions.ExpenseHoldedRequeued, Arg.Any<string>(), reportId,
            Arg.Any<string>(), actorId);
    }

    [HumansFact]
    public async Task RequeueHoldedPushWithResultAsync_Fails_WhenThereIsNothingStuck()
    {
        var reportId = Guid.NewGuid();
        _repo.RequeueOutboxForReportAsync(reportId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.RequeueHoldedPushWithResultAsync(
            reportId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        await _auditLog.DidNotReceiveWithAnyArgs()
            .LogAsync(null!, null!, default, null!, Guid.Empty);
    }

    [HumansFact]
    public async Task CreateIncomingDoc_PersistsContactLinkBeforeDocCreate_SoRetryReusesContact()
    {
        // Even when doc-create fails, the contact id must already be persisted, so the retry
        // reuses it (update mode) instead of creating a DUPLICATE creditor contact.
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HoldedTransientException("timeout")));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        // Contact link persisted (with the upserted id, num still null) BEFORE the failed create.
        await _repo.Received(1).SetHoldedContactLinkAsync(
            report.Id, "holded-contact-1", null, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PermanentException_MarkFailedPermanently_NotMarkedProcessed()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HoldedPermanentException(422, "body", "Unprocessable entity")));

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id,
            Arg.Any<string>(),
            Now,
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().SetLineHoldedDocIdAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().MarkOutboxProcessedAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().IncrementOutboxRetryAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PayeeIban_NeverAppearsInLogMessages()
    {
        // Use a logger that captures structured log args
        var logger = Substitute.For<ILogger<ExpenseReportBackgroundProcessor>>();
        var sut = new ExpenseReportBackgroundProcessor(
            _repo, _fileStorage, _userService, _holdedClient, _holdedFinance, _holdedFinanceWrite,
            Substitute.For<IAuditLogService>(), _clock, logger);

        var report = MakeReport() with { PayeeIban = "ES9121000418450200051332" };

        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-iban-test");

        await sut.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        // Verify the raw IBAN never appears as a log argument
        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("ES9121000418450200051332")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_SubmitterMergedAway_BindsTheCreditorToTheSurvivor()
    {
        // The report stays under the archived id, but the human is the survivor now, so
        // one member keeps one Holded contact rather than a second one under the old id.
        var survivorId = Guid.NewGuid();
        var survivor = new User { Id = survivorId, DisplayName = "Alice Smith" };
        _userService.GetUserInfoAsync(SubmitterId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MinimalUserInfo(survivor) with { MergedUserIds = [SubmitterId] }));
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), 100, Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("holded-doc-1");

        await _backgroundProcessor.DrainHoldedOutboxAsync(BatchSize, TestContext.Current.CancellationToken);

        await _holdedFinanceWrite.Received(1).EnsureCreditorContactAsync(
            survivorId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).GetForSubmitterAsync(survivorId, Arg.Any<CancellationToken>());
        await _repo.Received(1).GetForSubmitterAsync(SubmitterId, Arg.Any<CancellationToken>());
    }
}
