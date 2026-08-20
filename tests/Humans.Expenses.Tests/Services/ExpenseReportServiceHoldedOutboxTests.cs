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
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Expenses.Tests.Services;

public class ExpenseReportServiceHoldedOutboxTests
{
    private const int BatchSize = 100;

    private readonly IExpenseRepository _repo;
    private readonly IBudgetServiceRead _budgetService;
    private readonly IUserService _userService;
    private readonly IHoldedClient _holdedClient;
    private readonly IHoldedFinanceService _holdedFinance;
    private readonly IFileStorage _fileStorage;
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock;
    private readonly ExpenseReportService _sut;

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
        _budgetService = Substitute.For<IBudgetServiceRead>();
        _userService = Substitute.For<IUserService>();
        _holdedClient = Substitute.For<IHoldedClient>();
        // The drain now self-guards on the API key instead of the job doing it, so every test
        // that expects a push has to say the client is configured.
        _holdedClient.IsConfigured.Returns(true);
        _fileStorage = Substitute.For<IFileStorage>();
        _clock = new FakeClock(Now);

        _budgetService.GetCategoryByIdAsync(CategoryId)
            .Returns(_category);

        var submitter = new User { Id = SubmitterId, DisplayName = "Alice Smith" };
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [SubmitterId] = MinimalUserInfo(submitter) }));

        // Contact enrichment now lives in Finance: ExpenseReportService delegates to
        // EnsureCreditorContactAsync for the contact id, then resolves its supplier-account number.
        _holdedFinance = Substitute.For<IHoldedFinanceService>();
        _holdedFinance.EnsureCreditorContactAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("holded-contact-1");
        _holdedClient.GetContactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "holded-contact-1", SupplierAccountNum = 40000050 });

        _sut = new ExpenseReportService(
            _repo,
            _fileStorage,
            _budgetService,
            Substitute.For<ITeamService>(),
            _userService,
            _auditLog,
            _holdedClient,
            _holdedFinance,
            _clock,
            Substitute.For<ILogger<ExpenseReportService>>(),
            Options.Create(new TravelReimbursementConfig()));
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static ExpenseReportDto MakeReport(string? holdedDocId = null) => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = SubmitterId,
        BudgetCategoryId = CategoryId,
        BudgetYearId = Guid.NewGuid(),
        Status = ExpenseReportStatus.Approved,
        PayeeName = "Alice Smith",
        PayeeIban = "",
        Total = 0m,
        SubmittedAt = Instant.FromUtc(2026, 5, 1, 10, 0),
        CreatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
        UpdatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
        Note = "Team expenses",
        HoldedDocId = holdedDocId,
        Lines = new List<ExpenseLineDto>(),
    };

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

    // Replaces UserInfoStubHelpers.ToUserInfo(), which reads through an in-memory
    // UsersDbContext a section test project cannot see. Only the no-argument shape was
    // ever used here, and it is two lines.
    private static UserInfo MinimalUserInfo(User user) =>
        UserInfo.Create(user, [], [], [], profile: null, [], [], [], []);

    private static HoldedExpenseOutboxEvent MakeEvent(
        Guid reportId, HoldedExpenseOutboxEventType eventType) => new()
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            EventType = eventType,
            OccurredAt = Now,
        };

    // ─── empty queue ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task EmptyQueue_NoClientCalls()
    {
        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(null!, Arg.Any<CancellationToken>());
    }

    // ─── CreateIncomingDoc happy path ──────────────────────────────────────────

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                string.Equals(i.ContactName, "Alice Smith", StringComparison.Ordinal) &&
                string.Equals(i.Description, "Team expenses", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        await _repo.Received(1).SetHoldedDocIdAsync(
            report.Id, holdedDocId, Now, Arg.Any<CancellationToken>());
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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

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
        // Tags are dead on the write side (Peter, 2026-08-10) — the doc is booked to the right
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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.Lines.Count == 1 &&
                string.Equals(i.Lines[0].AccountId, "holded-acc-629001", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i => i.Lines.Count == 1),
            Arg.Any<CancellationToken>());
    }

    // ─── CreateIncomingDoc with attachments ───────────────────────────────────

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
        const string holdedDocId = "holded-doc-with-attachments";

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(holdedDocId);

        _fileStorage.TryReadAsync(
                $"uploads/expense-attachments/{attachment1.Id}.pdf",
                Arg.Any<CancellationToken>())
            .Returns([1, 2]);
        _fileStorage.TryReadAsync(
                $"uploads/expense-attachments/{attachment2.Id}.jpg",
                Arg.Any<CancellationToken>())
            .Returns([3, 4]);

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _holdedClient.Received(1).UploadAttachmentAsync(
            holdedDocId,
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "receipt1.pdf" && a.ContentType == "application/pdf"),
            Arg.Any<CancellationToken>());

        await _holdedClient.Received(1).UploadAttachmentAsync(
            holdedDocId,
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "receipt2.jpg" && a.ContentType == "image/jpeg"),
            Arg.Any<CancellationToken>());

        await _repo.Received(1).SetHoldedDocIdAsync(
            report.Id, holdedDocId, Now, Arg.Any<CancellationToken>());
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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .UploadAttachmentAsync(null!, null!, Arg.Any<CancellationToken>());
        await _repo.Received(1).SetHoldedDocIdAsync(
            report.Id, "doc-no-att", Now, Arg.Any<CancellationToken>());
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

        var act = async () => await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be read from storage*");

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .UploadAttachmentAsync(null!, null!, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkOutboxProcessedAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_HoldedDocIdAlreadySet_SkipsCreateAndOnlyMarksProcessed()
    {
        // Idempotency guard: if a previous retry already issued the Holded document
        // (and persisted HoldedDocId early) but failed during the attachment upload
        // loop, the next drain pass must NOT call CreatePurchaseDocumentAsync again.
        var report = MakeReport(holdedDocId: "doc-prev-retry");
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(null!, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .SetHoldedDocIdAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    // ─── UpdateIncomingDocTag: v2 has no tag-update endpoint, so this now just drains ──────────

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(null!, Arg.Any<CancellationToken>());
    }

    // ─── Transient error ──────────────────────────────────────────────────────

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).IncrementOutboxRetryAsync(
            outboxEvent.Id, "timeout", Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().SetHoldedDocIdAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().MarkOutboxProcessedAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkOutboxFailedPermanentlyAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(0, 2)]
    [Xunit.InlineData(1, 4)]
    [Xunit.InlineData(3, 16)]
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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id,
            Arg.Is<string>(s => s.Contains("10 attempts") && s.Contains("timeout")),
            Now, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs()
            .IncrementOutboxRetryAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.ExpenseHoldedFailed, Arg.Any<string>(), report.Id,
            Arg.Any<string>(), Arg.Any<string>(), null, null);
    }

    [HumansFact]
    public async Task NoApiKeyConfigured_DrainDoesNotTouchTheQueue()
    {
        // Every call would 401 — a permanent error — so draining would write off the whole queue.
        _holdedClient.IsConfigured.Returns(false);

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceiveWithAnyArgs()
            .GetUnprocessedOutboxAsync(default, 0, Arg.Any<CancellationToken>());
    }

    // ─── attachment idempotency ───────────────────────────────────────────────

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.ExpenseHoldedPushed, Arg.Any<string>(), report.Id,
            Arg.Is<string>(s => s.Contains("holded-doc-42")), Arg.Any<string>(), null, null);
    }

    // ─── sync state reported to finance ───────────────────────────────────────

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsQueued_ForAFreshEvent()
    {
        var report = MakeReport();
        _repo.GetLatestOutboxForReportAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc));

        var timeline = await _sut.GetHoldedTimelineAsync(report, Xunit.TestContext.Current.CancellationToken);

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

        var timeline = await _sut.GetHoldedTimelineAsync(report, Xunit.TestContext.Current.CancellationToken);

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

        var timeline = await _sut.GetHoldedTimelineAsync(report, Xunit.TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.Failed);
        timeline.CanRetry.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsNotConfigured_RatherThanQueued_WhenThereIsNoApiKey()
    {
        // Otherwise a PR-preview environment shows "Queued" forever for a queue nothing drains.
        _holdedClient.IsConfigured.Returns(false);
        var report = MakeReport();
        _repo.GetLatestOutboxForReportAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc));

        var timeline = await _sut.GetHoldedTimelineAsync(report, Xunit.TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.NotConfigured);
    }

    [HumansFact]
    public async Task GetHoldedTimelineAsync_ReportsNotQueued_WhenNoPushWasEverQueued()
    {
        var timeline = await _sut.GetHoldedTimelineAsync(MakeReport(), Xunit.TestContext.Current.CancellationToken);

        timeline!.SyncState.Should().Be(ExpenseHoldedSyncState.NotQueued);
        timeline.CanRetry.Should().BeFalse();
    }

    // ─── manual re-queue ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task RequeueHoldedPushWithResultAsync_RequeuesAndAudits()
    {
        var reportId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        _repo.RequeueOutboxForReportAsync(reportId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.RequeueHoldedPushWithResultAsync(
            reportId, actorId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        await _auditLog.Received(1).LogAsync(
            AuditAction.ExpenseHoldedRequeued, Arg.Any<string>(), reportId,
            Arg.Any<string>(), actorId, null, null);
    }

    [HumansFact]
    public async Task RequeueHoldedPushWithResultAsync_Fails_WhenThereIsNothingStuck()
    {
        var reportId = Guid.NewGuid();
        _repo.RequeueOutboxForReportAsync(reportId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.RequeueHoldedPushWithResultAsync(
            reportId, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        await _auditLog.DidNotReceiveWithAnyArgs()
            .LogAsync(default, null!, default, null!, Guid.Empty, null, null);
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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        // Contact link persisted (with the upserted id, num still null) BEFORE the failed create.
        await _repo.Received(1).SetHoldedContactLinkAsync(
            report.Id, "holded-contact-1", null, Now, Arg.Any<CancellationToken>());
    }

    // ─── Permanent error ──────────────────────────────────────────────────────

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

        await _sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id,
            Arg.Any<string>(),
            Now,
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().SetHoldedDocIdAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().MarkOutboxProcessedAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().IncrementOutboxRetryAsync(Guid.Empty, null!, default, Arg.Any<CancellationToken>());
    }

    // ─── IBAN never logged ────────────────────────────────────────────────────

    [HumansFact]
    public async Task PayeeIban_NeverAppearsInLogMessages()
    {
        // Use a logger that captures structured log args
        var logger = Substitute.For<ILogger<ExpenseReportService>>();
        var sut = new ExpenseReportService(
            _repo,
            _fileStorage,
            _budgetService,
            Substitute.For<ITeamService>(),
            _userService,
            Substitute.For<IAuditLogService>(),
            _holdedClient,
            _holdedFinance,
            _clock,
            logger,
            Options.Create(new TravelReimbursementConfig()));

        var report = MakeReport() with { PayeeIban = "ES9121000418450200051332" };

        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-iban-test");

        await sut.DrainHoldedOutboxAsync(BatchSize, Xunit.TestContext.Current.CancellationToken);

        // Verify the raw IBAN never appears as a log argument
        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("ES9121000418450200051332")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
