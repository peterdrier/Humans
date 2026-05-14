using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Expenses;

public class ExpenseReportServiceHoldedOutboxTests
{
    private const int BatchSize = 100;

    private readonly IExpenseRepository _repo;
    private readonly IBudgetService _budgetService;
    private readonly IUserService _userService;
    private readonly IHoldedClient _holdedClient;
    private readonly IFileStorage _fileStorage;
    private readonly FakeClock _clock;
    private readonly ExpenseReportService _sut;

    private static readonly Instant Now = Instant.FromUtc(2026, 5, 10, 12, 0);
    private static readonly Guid SubmitterId = Guid.NewGuid();
    private static readonly Guid CategoryId = Guid.NewGuid();

    private readonly BudgetGroup _group = new()
    {
        Id = Guid.NewGuid(),
        Name = "Camp Build",
    };

    private readonly BudgetCategory _category;

    public ExpenseReportServiceHoldedOutboxTests()
    {
        _category = new BudgetCategory
        {
            Id = CategoryId,
            Name = "Camp",
            BudgetGroupId = _group.Id,
            BudgetGroup = _group,
        };

        _repo = Substitute.For<IExpenseRepository>();
        _budgetService = Substitute.For<IBudgetService>();
        _userService = Substitute.For<IUserService>();
        _holdedClient = Substitute.For<IHoldedClient>();
        _fileStorage = Substitute.For<IFileStorage>();
        _clock = new FakeClock(Now);

        _budgetService.GetCategoryByIdAsync(CategoryId)
            .Returns(_category);

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>
            {
                [SubmitterId] = new User { Id = SubmitterId, DisplayName = "Alice Smith" },
            });

        _sut = new ExpenseReportService(
            _repo,
            _fileStorage,
            _budgetService,
            Substitute.For<ITeamService>(),
            _userService,
            Substitute.For<IAuditLogService>(),
            _holdedClient,
            _clock,
            Substitute.For<ILogger<ExpenseReportService>>());
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
        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<HoldedExpenseOutboxEvent>());

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(default!, default);
        await _holdedClient.DidNotReceiveWithAnyArgs()
            .UpdatePurchaseDocumentTagsAsync(default!, default!, default);
    }

    // ─── CreateIncomingDoc happy path ──────────────────────────────────────────

    [HumansFact]
    public async Task CreateIncomingDoc_HappyPath_ClientCalledAndDocIdPersisted()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        const string holdedDocId = "holded-doc-999";

        _repo.GetUnprocessedOutboxAsync(100, Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(holdedDocId);

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i =>
                i.ContactName == "Alice Smith" &&
                i.Tags.Contains("camp-build-camp") &&
                i.Description == "Team expenses"),
            Arg.Any<CancellationToken>());

        await _repo.Received(1).SetHoldedDocIdAsync(
            report.Id, holdedDocId, Now, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateIncomingDoc_TagFormatIsGroupSlugDashCategorySlug()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        // group "Camp Build" → "camp-build", category "Camp" → "camp"
        await _holdedClient.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i => i.Tags.SequenceEqual(new[] { "camp-build-camp" })),
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
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Line A", Amount = 10m, SortOrder = 1, AttachmentId = attachment1.Id, Attachment = attachment1 },
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Line B", Amount = 20m, SortOrder = 2, AttachmentId = attachment2.Id, Attachment = attachment2 },
            }
        };

        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);
        const string holdedDocId = "holded-doc-with-attachments";

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(holdedDocId);

        _fileStorage.TryReadAsync(
                $"uploads/expense-attachments/{attachment1.Id}.pdf",
                Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2 });
        _fileStorage.TryReadAsync(
                $"uploads/expense-attachments/{attachment2.Id}.jpg",
                Arg.Any<CancellationToken>())
            .Returns(new byte[] { 3, 4 });

        await _sut.DrainHoldedOutboxAsync(BatchSize);

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
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "No receipt", Amount = 5m, SortOrder = 1, AttachmentId = null, Attachment = null },
            }
        };

        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-no-att");

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .UploadAttachmentAsync(default!, default!, default);
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
                new() { Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Line A", Amount = 10m, SortOrder = 1, AttachmentId = attachment.Id, Attachment = attachment },
            }
        };
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-missing-bytes");
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);

        var act = async () => await _sut.DrainHoldedOutboxAsync(BatchSize);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be read from storage*");

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .UploadAttachmentAsync(default!, default!, default);
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkOutboxProcessedAsync(default, default, default);
    }

    [HumansFact]
    public async Task CreateIncomingDoc_HoldedDocIdAlreadySet_SkipsCreateAndOnlyMarksProcessed()
    {
        // Idempotency guard: if a previous retry already issued the Holded document
        // (and persisted HoldedDocId early) but failed during the attachment upload
        // loop, the next drain pass must NOT call CreatePurchaseDocumentAsync again.
        var report = MakeReport(holdedDocId: "doc-prev-retry");
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(default!, default);
        await _repo.DidNotReceiveWithAnyArgs()
            .SetHoldedDocIdAsync(default, default!, default, default);
        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());
    }

    // ─── UpdateIncomingDocTag happy path ──────────────────────────────────────

    [HumansFact]
    public async Task UpdateIncomingDocTag_HappyPath_TagsUpdatedAndMarkedProcessed()
    {
        var report = MakeReport(holdedDocId: "holded-existing-doc");
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.UpdateIncomingDocTag);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        await _holdedClient.Received(1).UpdatePurchaseDocumentTagsAsync(
            "holded-existing-doc",
            Arg.Is<IReadOnlyList<string>>(tags => tags.SequenceEqual(new[] { "camp-build-camp" })),
            Arg.Any<CancellationToken>());

        await _repo.Received(1).MarkOutboxProcessedAsync(
            outboxEvent.Id, Now, Arg.Any<CancellationToken>());

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .CreatePurchaseDocumentAsync(default!, default);
    }

    // ─── Transient error ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task TransientException_IncrementRetry_NotMarkedProcessed()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HoldedTransientException("timeout")));

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        await _repo.Received(1).IncrementOutboxRetryAsync(
            outboxEvent.Id, "timeout", Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().SetHoldedDocIdAsync(default, default!, default, default);
        await _repo.DidNotReceiveWithAnyArgs().MarkOutboxProcessedAsync(default, default, default);
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkOutboxFailedPermanentlyAsync(default, default!, default, default);
    }

    // ─── Permanent error ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task PermanentException_MarkFailedPermanently_NotMarkedProcessed()
    {
        var report = MakeReport();
        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HoldedPermanentException(422, "body", "Unprocessable entity")));

        await _sut.DrainHoldedOutboxAsync(BatchSize);

        await _repo.Received(1).MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id,
            Arg.Any<string>(),
            Now,
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().SetHoldedDocIdAsync(default, default!, default, default);
        await _repo.DidNotReceiveWithAnyArgs().MarkOutboxProcessedAsync(default, default, default);
        await _repo.DidNotReceiveWithAnyArgs().IncrementOutboxRetryAsync(default, default!, default);
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
            _clock,
            logger);

        var report = MakeReport() with { PayeeIban = "ES9121000418450200051332" };

        var outboxEvent = MakeEvent(report.Id, HoldedExpenseOutboxEventType.CreateIncomingDoc);

        _repo.GetUnprocessedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxEvent]);
        _repo.GetByIdAsync(report.Id, Arg.Any<CancellationToken>())
            .Returns(report);
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-iban-test");

        await sut.DrainHoldedOutboxAsync(BatchSize);

        // Verify the raw IBAN never appears as a log argument
        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("ES9121000418450200051332")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
