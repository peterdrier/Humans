using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Expenses.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Expenses.Tests.Data;

public class ExpenseRepositoryTests
{
    private readonly IDbContextFactory<ExpensesDbContext> _factory;
    private readonly IExpenseRepository _sut;

    public ExpenseRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<ExpensesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbContextFactory<ExpensesDbContext>(options);
        _sut = new ExpenseRepository(_factory);
    }

    [HumansFact]
    public async Task GetByIdAsync_ReturnsRecord_WhenExists()
    {
        var id = Guid.NewGuid();
        await Seed(new ExpenseReport
        {
            Id = id,
            SubmitterUserId = Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = ExpenseReportStatus.Draft,
            CreatedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
        });

        var got = await _sut.GetByIdAsync(id, Xunit.TestContext.Current.CancellationToken);
        got.Should().NotBeNull();
        got.Id.Should().Be(id);
    }

    [HumansFact]
    public async Task GetForSubmitterAsync_ScopesByUser()
    {
        var meId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await Seed(MakeReport(submitter: meId), MakeReport(submitter: otherId));

        var mine = await _sut.GetForSubmitterAsync(meId, Xunit.TestContext.Current.CancellationToken);
        mine.Should().HaveCount(1);
        mine[0].SubmitterUserId.Should().Be(meId);
    }

    [HumansFact]
    public async Task AddDraftAsync_PersistsReport()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded.Should().NotBeNull();
        loaded.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task AddLineAsync_AppendsLine_AndUpdatesTotal()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);

        var ok = await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = Guid.NewGuid(), Description = "x", Amount = 12.50m }, Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Should().HaveCount(1);
        loaded.Total.Should().Be(12.50m);
    }

    [HumansFact]
    public async Task RemoveLineAsync_RemovesAndRecomputesTotal()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);
        var lineId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = lineId, Description = "a", Amount = 10m }, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = Guid.NewGuid(), Description = "b", Amount = 20m }, Xunit.TestContext.Current.CancellationToken);

        var removed = await _sut.RemoveLineAsync(report.Id, lineId, Xunit.TestContext.Current.CancellationToken);
        removed.Should().NotBeNull();

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Should().HaveCount(1);
        loaded.Total.Should().Be(20m);
    }

    [HumansFact]
    public async Task AddLineAsync_ProofRow_DoesNotChangeTotal()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);
        var invoiceId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = invoiceId, Description = "invoice", Amount = 1000m, LineType = ExpenseLineType.Invoice }, Xunit.TestContext.Current.CancellationToken);

        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = Guid.NewGuid(), Description = "proof", Amount = 400m, ParentLineId = invoiceId }, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(1000m);
        loaded.Lines.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task UpdateLineAsync_ProofRow_DoesNotChangeTotal()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);
        var invoiceId = Guid.NewGuid();
        var proofId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = invoiceId, Description = "invoice", Amount = 1000m, LineType = ExpenseLineType.Invoice }, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = proofId, Description = "proof", Amount = 400m, ParentLineId = invoiceId }, Xunit.TestContext.Current.CancellationToken);

        await _sut.UpdateLineAsync(report.Id,
            new ExpenseLine { Id = proofId, Description = "proof edited", Amount = 999m }, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(1000m);
        loaded.Lines.Single(l => l.Id == proofId).Amount.Should().Be(999m);
    }

    [HumansFact]
    public async Task RemoveLineAsync_ProofRow_DoesNotChangeTotal()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);
        var invoiceId = Guid.NewGuid();
        var proofId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = invoiceId, Description = "invoice", Amount = 1000m, LineType = ExpenseLineType.Invoice }, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = proofId, Description = "proof", Amount = 400m, ParentLineId = invoiceId }, Xunit.TestContext.Current.CancellationToken);

        var removed = await _sut.RemoveLineAsync(report.Id, proofId, Xunit.TestContext.Current.CancellationToken);
        removed.Should().NotBeNull();

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Total.Should().Be(1000m);
        loaded.Lines.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task RemoveLineAsync_InvoiceLine_RemovesProofsAndAttachmentRows_InOneSave()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);
        var invoiceId = Guid.NewGuid();
        var proofId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = invoiceId, Description = "invoice", Amount = 1000m, LineType = ExpenseLineType.Invoice }, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = proofId, Description = "proof", Amount = 400m, ParentLineId = invoiceId }, Xunit.TestContext.Current.CancellationToken);

        var proofAttachment = new ExpenseAttachment
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "proof.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
        };
        await _sut.AddAttachmentAsync(proofAttachment, Xunit.TestContext.Current.CancellationToken);
        await _sut.SetLineAttachmentAsync(proofId, proofAttachment.Id, Xunit.TestContext.Current.CancellationToken);

        var removed = await _sut.RemoveLineAsync(report.Id, invoiceId, Xunit.TestContext.Current.CancellationToken);

        removed.Should().NotBeNull();
        removed.Should().ContainSingle(a => a.Id == proofAttachment.Id);
        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.Should().BeEmpty();
        loaded.Total.Should().Be(0m);
        await using var ctx = await _factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        (await ctx.ExpenseAttachments.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task SetLineAttachmentAsync_LinksAttachment()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report, Xunit.TestContext.Current.CancellationToken);
        var lineId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = lineId, Description = "x", Amount = 1m }, Xunit.TestContext.Current.CancellationToken);

        var attachId = await _sut.AddAttachmentAsync(new ExpenseAttachment
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "r.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
        }, Xunit.TestContext.Current.CancellationToken);

        await _sut.SetLineAttachmentAsync(lineId, attachId, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Lines.First().AttachmentId.Should().Be(attachId);
        loaded.Lines.First().Attachment.Should().NotBeNull();
    }

    [HumansFact]
    public async Task SubmitAsync_FlipsStatus_AndStampsSubmittedAt()
    {
        var r = MakeReport();
        await _sut.AddDraftAsync(r, Xunit.TestContext.Current.CancellationToken);
        await _sut.AddLineAsync(r.Id,
            new ExpenseLine { Id = Guid.NewGuid(), Description = "x", Amount = 5m }, Xunit.TestContext.Current.CancellationToken);
        var attachId = await _sut.AddAttachmentAsync(NewAttachment(), Xunit.TestContext.Current.CancellationToken);
        var line = (await _sut.GetByIdAsync(r.Id, Xunit.TestContext.Current.CancellationToken))!.Lines.First();
        await _sut.SetLineAttachmentAsync(line.Id, attachId, Xunit.TestContext.Current.CancellationToken);

        var ok = await _sut.SubmitAsync(r.Id, "Alice", "ES9121000418450200051332",
            Instant.FromUtc(2026, 5, 2, 9, 0), Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetByIdAsync(r.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
        loaded.PayeeName.Should().Be("Alice");
        loaded.PayeeIban.Should().Be("ES9121000418450200051332");
        loaded.SubmittedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task ApproveAsync_StampsApproval_AndInsertsOutboxRow()
    {
        var r = MakeReport(status: ExpenseReportStatus.Submitted);
        await Seed(r);
        var outboxId = Guid.NewGuid();

        var ok = await _sut.ApproveAsync(r.Id, Guid.NewGuid(), null, null,
            Instant.FromUtc(2026, 5, 3, 12, 0), outboxId, Xunit.TestContext.Current.CancellationToken);
        ok.Should().BeTrue();

        var loaded = await _sut.GetByIdAsync(r.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
        loaded.ApprovedAt.Should().NotBeNull();

        await using var ctx = await _factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var ev = await ctx.HoldedExpenseOutboxEvents.FirstAsync(e => e.Id == outboxId, Xunit.TestContext.Current.CancellationToken);
        ev.ExpenseReportId.Should().Be(r.Id);
        ev.EventType.Should().Be(HoldedExpenseOutboxEventType.CreateIncomingDoc);
    }

    [HumansFact]
    public async Task GetUnprocessedOutboxAsync_FiltersAndLimits()
    {
        var ev1 = NewOutbox();
        var ev2 = NewOutbox(processedAt: Instant.FromUtc(2026, 5, 5, 0, 0));
        var ev3 = NewOutbox(failedPermanently: true);
        var ev4 = NewOutbox();
        await SeedOutbox(ev1, ev2, ev3, ev4);

        var got = await _sut.GetUnprocessedOutboxAsync(Instant.FromUtc(2026, 5, 10, 0, 0), limit: 10, ct: Xunit.TestContext.Current.CancellationToken);
        got.Should().HaveCount(2);
        got.Select(e => e.Id).Should().BeEquivalentTo([ev1.Id, ev4.Id]);
    }

    [HumansFact]
    public async Task GetUnprocessedOutboxAsync_HoldsBackEventsStillInsideTheirBackoff()
    {
        var now = Instant.FromUtc(2026, 5, 10, 0, 0);
        var due = NewOutbox(nextRetryAt: now - Duration.FromMinutes(1), retryCount: 2);
        var notDue = NewOutbox(nextRetryAt: now + Duration.FromMinutes(1), retryCount: 2);
        var fresh = NewOutbox();
        await SeedOutbox(due, notDue, fresh);

        var got = await _sut.GetUnprocessedOutboxAsync(now, limit: 10, ct: Xunit.TestContext.Current.CancellationToken);

        got.Select(e => e.Id).Should().BeEquivalentTo([due.Id, fresh.Id]);
    }

    [HumansFact]
    public async Task GetLatestOutboxForReportAsync_IgnoresTagUpdates()
    {
        // A tag-update event marks itself processed immediately; if it counted as "the latest
        // event" it would report a failed create as Pushed.
        var reportId = Guid.NewGuid();
        var create = NewOutbox(reportId, failedPermanently: true, lastError: "boom",
            occurredAt: Instant.FromUtc(2026, 5, 1, 0, 0));
        var tag = NewOutbox(reportId, processedAt: Instant.FromUtc(2026, 5, 2, 0, 0),
            eventType: HoldedExpenseOutboxEventType.UpdateIncomingDocTag,
            occurredAt: Instant.FromUtc(2026, 5, 2, 0, 0));
        await SeedOutbox(create, tag);

        var got = await _sut.GetLatestOutboxForReportAsync(reportId, Xunit.TestContext.Current.CancellationToken);

        got!.Id.Should().Be(create.Id);
    }

    [HumansFact]
    public async Task RequeueOutboxForReportAsync_ClearsWriteOffAndBackoff()
    {
        var reportId = Guid.NewGuid();
        var writtenOff = NewOutbox(reportId, failedPermanently: true, retryCount: 10,
            lastError: "gave up", processedAt: Instant.FromUtc(2026, 5, 5, 0, 0));
        await SeedOutbox(writtenOff);

        var ok = await _sut.RequeueOutboxForReportAsync(reportId, Xunit.TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        var reloaded = await _sut.GetLatestOutboxForReportAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        reloaded!.FailedPermanently.Should().BeFalse();
        reloaded.ProcessedAt.Should().BeNull();
        reloaded.RetryCount.Should().Be(0);
        reloaded.LastError.Should().BeNull();
        reloaded.NextRetryAt.Should().BeNull();
    }

    [HumansFact]
    public async Task RequeueOutboxForReportAsync_AlsoClearsAnEventMerelyWaitingOutABackoff()
    {
        var reportId = Guid.NewGuid();
        await SeedOutbox(NewOutbox(reportId, retryCount: 3, lastError: "timeout",
            nextRetryAt: Instant.FromUtc(2026, 5, 20, 0, 0)));

        var ok = await _sut.RequeueOutboxForReportAsync(reportId, Xunit.TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        var reloaded = await _sut.GetLatestOutboxForReportAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        reloaded!.NextRetryAt.Should().BeNull();
        reloaded.RetryCount.Should().Be(0);
    }

    [HumansFact]
    public async Task RequeueOutboxForReportAsync_ReturnsFalse_WhenNothingIsStuck()
    {
        var reportId = Guid.NewGuid();
        await SeedOutbox(NewOutbox(reportId, processedAt: Instant.FromUtc(2026, 5, 5, 0, 0)));

        var ok = await _sut.RequeueOutboxForReportAsync(reportId, Xunit.TestContext.Current.CancellationToken);

        ok.Should().BeFalse();
    }

    [HumansFact]
    public async Task CountFailedOutboxAsync_CountsOnlyWrittenOffEvents()
    {
        var a = MakeReport(status: ExpenseReportStatus.Approved);
        var b = MakeReport(status: ExpenseReportStatus.Approved);
        var c = MakeReport(status: ExpenseReportStatus.Approved);
        var d = MakeReport(status: ExpenseReportStatus.Approved);
        await Seed(a, b, c, d);
        await SeedOutbox(
            NewOutbox(a.Id, failedPermanently: true),
            NewOutbox(b.Id, failedPermanently: true),
            NewOutbox(c.Id, retryCount: 2, lastError: "timeout"),
            NewOutbox(d.Id, processedAt: Instant.FromUtc(2026, 5, 5, 0, 0)));

        var count = await _sut.CountFailedOutboxAsync(Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(2);
    }

    [HumansFact]
    public async Task CountFailedOutboxAsync_SkipsReportsFinanceCannotAction()
    {
        // Withdrawn after approval: absent from the review queue, and RequeueHoldedPush refuses it.
        // Counting it would leave a banner nobody can clear.
        var withdrawn = MakeReport(status: ExpenseReportStatus.Withdrawn);
        var approved = MakeReport(status: ExpenseReportStatus.Approved);
        await Seed(withdrawn, approved);
        await SeedOutbox(
            NewOutbox(withdrawn.Id, failedPermanently: true),
            NewOutbox(approved.Id, failedPermanently: true,
                eventType: HoldedExpenseOutboxEventType.UpdateIncomingDocTag),
            NewOutbox(approved.Id, failedPermanently: true));

        var count = await _sut.CountFailedOutboxAsync(Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(1);
    }

    [HumansFact]
    public async Task MarkOutboxFailedPermanentlyAsync_CountsTheAttemptThatFailed()
    {
        // The tenth transient failure takes the write-off branch, not IncrementOutboxRetryAsync, so
        // the write-off is what has to record it — otherwise the timeline says 9 and the error 10.
        var reportId = Guid.NewGuid();
        var ev = NewOutbox(reportId, retryCount: 9);
        await SeedOutbox(ev);

        await _sut.MarkOutboxFailedPermanentlyAsync(
            ev.Id, "Gave up after 10 attempts.", Instant.FromUtc(2026, 5, 6, 0, 0),
            Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _sut.GetLatestOutboxForReportAsync(reportId, Xunit.TestContext.Current.CancellationToken);
        reloaded!.RetryCount.Should().Be(10);
        reloaded.FailedPermanently.Should().BeTrue();
    }

    [HumansFact]
    public async Task MarkAttachmentPushedAsync_StampsTheUploadTime()
    {
        var attachmentId = await _sut.AddAttachmentAsync(new ExpenseAttachment
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "receipt.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
        }, Xunit.TestContext.Current.CancellationToken);
        var pushedAt = Instant.FromUtc(2026, 5, 6, 9, 0);

        await _sut.MarkAttachmentPushedAsync(attachmentId, pushedAt, Xunit.TestContext.Current.CancellationToken);

        await using var ctx = await _factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var reloaded = await ctx.ExpenseAttachments.FirstAsync(a => a.Id == attachmentId, Xunit.TestContext.Current.CancellationToken);
        reloaded.HoldedUploadedAt.Should().Be(pushedAt);
    }

    [HumansFact]
    public async Task SetHoldedDocIdAsync_PersistsHoldedDocIdAndUpdatedAt()
    {
        // Persistence is intentionally separate from outbox-event marking — the
        // service writes HoldedDocId immediately after the Holded create call and
        // marks the outbox event processed only after the full upload chain succeeds
        // (idempotency: a retry after a failed upload reuses the persisted doc id).
        var report = MakeReport(status: ExpenseReportStatus.Approved);
        await Seed(report);
        var updatedAt = Instant.FromUtc(2026, 5, 5, 1, 0);

        await _sut.SetHoldedDocIdAsync(report.Id, "doc-123", updatedAt, Xunit.TestContext.Current.CancellationToken);

        var loaded = await _sut.GetByIdAsync(report.Id, Xunit.TestContext.Current.CancellationToken);
        loaded!.HoldedDocId.Should().Be("doc-123");
        loaded.UpdatedAt.Should().Be(updatedAt);
    }

    private async Task Seed(params ExpenseReport[] reports)
    {
        await using var ctx = await _factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        await ctx.ExpenseReports.AddRangeAsync(reports);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task SeedOutbox(params HoldedExpenseOutboxEvent[] events)
    {
        await using var ctx = await _factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        await ctx.HoldedExpenseOutboxEvents.AddRangeAsync(events);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private static ExpenseReport MakeReport(
        Guid? submitter = null,
        ExpenseReportStatus status = ExpenseReportStatus.Draft)
    {
        var now = Instant.FromUtc(2026, 5, 1, 0, 0);
        return new ExpenseReport
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitter ?? Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ExpenseAttachment NewAttachment() => new()
    {
        Id = Guid.NewGuid(),
        OriginalFileName = "r.pdf",
        Extension = ".pdf",
        ContentType = "application/pdf",
        SizeBytes = 100,
        UploadedByUserId = Guid.NewGuid(),
        UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
    };

    private static HoldedExpenseOutboxEvent NewOutbox(
        Guid? reportId = null,
        Instant? processedAt = null,
        bool failedPermanently = false,
        Instant? nextRetryAt = null,
        int retryCount = 0,
        string? lastError = null,
        HoldedExpenseOutboxEventType eventType = HoldedExpenseOutboxEventType.CreateIncomingDoc,
        Instant? occurredAt = null) => new()
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId ?? Guid.NewGuid(),
            EventType = eventType,
            OccurredAt = occurredAt ?? Instant.FromUtc(2026, 5, 1, 0, 0),
            ProcessedAt = processedAt,
            FailedPermanently = failedPermanently,
            NextRetryAt = nextRetryAt,
            RetryCount = retryCount,
            LastError = lastError
        };
}
