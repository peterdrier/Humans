using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Expenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories.Expenses;

public class ExpenseRepositoryTests
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly IExpenseRepository _sut;

    public ExpenseRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbContextFactory(options);
        _sut = new ExpenseRepository(_factory, NullLogger<ExpenseRepository>.Instance);
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

        var got = await _sut.GetByIdAsync(id);
        got.Should().NotBeNull();
        got!.Id.Should().Be(id);
    }

    [HumansFact]
    public async Task GetForSubmitterAsync_ScopesByUser()
    {
        var meId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await Seed(MakeReport(submitter: meId), MakeReport(submitter: otherId));

        var mine = await _sut.GetForSubmitterAsync(meId);
        mine.Should().HaveCount(1);
        mine[0].SubmitterUserId.Should().Be(meId);
    }

    [HumansFact]
    public async Task GetByStatusAsync_FiltersExactly()
    {
        await Seed(
            MakeReport(status: ExpenseReportStatus.Draft),
            MakeReport(status: ExpenseReportStatus.Submitted),
            MakeReport(status: ExpenseReportStatus.Approved));

        var submitted = await _sut.GetByStatusAsync(ExpenseReportStatus.Submitted);
        submitted.Should().HaveCount(1);
        submitted[0].Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task AddDraftAsync_PersistsReport()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report);

        var loaded = await _sut.GetByIdAsync(report.Id);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
    }

    [HumansFact]
    public async Task AddLineAsync_AppendsLine_AndUpdatesTotal()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report);

        var ok = await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = Guid.NewGuid(), Description = "x", Amount = 12.50m });
        ok.Should().BeTrue();

        var loaded = await _sut.GetByIdAsync(report.Id);
        loaded!.Lines.Should().HaveCount(1);
        loaded.Total.Should().Be(12.50m);
    }

    [HumansFact]
    public async Task RemoveLineAsync_RemovesAndRecomputesTotal()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report);
        var lineId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = lineId, Description = "a", Amount = 10m });
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = Guid.NewGuid(), Description = "b", Amount = 20m });

        var ok = await _sut.RemoveLineAsync(report.Id, lineId);
        ok.Should().BeTrue();

        var loaded = await _sut.GetByIdAsync(report.Id);
        loaded!.Lines.Should().HaveCount(1);
        loaded.Total.Should().Be(20m);
    }

    [HumansFact]
    public async Task SetLineAttachmentAsync_LinksAttachment()
    {
        var report = MakeReport();
        await _sut.AddDraftAsync(report);
        var lineId = Guid.NewGuid();
        await _sut.AddLineAsync(report.Id,
            new ExpenseLine { Id = lineId, Description = "x", Amount = 1m });

        var attachId = await _sut.AddAttachmentAsync(new ExpenseAttachment
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "r.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
        });

        await _sut.SetLineAttachmentAsync(lineId, attachId);

        var loaded = await _sut.GetByIdAsync(report.Id);
        loaded!.Lines.First().AttachmentId.Should().Be(attachId);
        loaded.Lines.First().Attachment.Should().NotBeNull();
    }

    [HumansFact]
    public async Task SubmitAsync_FlipsStatus_AndStampsSubmittedAt()
    {
        var r = MakeReport();
        await _sut.AddDraftAsync(r);
        await _sut.AddLineAsync(r.Id,
            new ExpenseLine { Id = Guid.NewGuid(), Description = "x", Amount = 5m });
        var attachId = await _sut.AddAttachmentAsync(NewAttachment());
        var line = (await _sut.GetByIdAsync(r.Id))!.Lines.First();
        await _sut.SetLineAttachmentAsync(line.Id, attachId);

        var ok = await _sut.SubmitAsync(r.Id, "Alice", "ES9121000418450200051332",
            Instant.FromUtc(2026, 5, 2, 9, 0));
        ok.Should().BeTrue();

        var loaded = await _sut.GetByIdAsync(r.Id);
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

        var ok = await _sut.ApproveAsync(r.Id, Guid.NewGuid(), null,
            Instant.FromUtc(2026, 5, 3, 12, 0), outboxId);
        ok.Should().BeTrue();

        var loaded = await _sut.GetByIdAsync(r.Id);
        loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
        loaded.ApprovedAt.Should().NotBeNull();

        await using var ctx = await _factory.CreateDbContextAsync();
        var ev = await ctx.HoldedExpenseOutboxEvents.FirstAsync(e => e.Id == outboxId);
        ev.ExpenseReportId.Should().Be(r.Id);
        ev.EventType.Should().Be(HoldedExpenseOutboxEventType.CreateIncomingDoc);
    }

    [HumansFact]
    public async Task MarkSepaSentAsync_FlipsAllInBatch()
    {
        var a = MakeReport(status: ExpenseReportStatus.Approved);
        var b = MakeReport(status: ExpenseReportStatus.Approved);
        var c = MakeReport(status: ExpenseReportStatus.Submitted); // not in batch
        await Seed(a, b, c);

        var flipped = await _sut.MarkSepaSentAsync(new[] { a.Id, b.Id },
            Instant.FromUtc(2026, 5, 4, 10, 0));
        flipped.Should().BeEquivalentTo(new[] { a.Id, b.Id });

        (await _sut.GetByIdAsync(a.Id))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
        (await _sut.GetByIdAsync(b.Id))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
        (await _sut.GetByIdAsync(c.Id))!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task GetUnprocessedOutboxAsync_FiltersAndLimits()
    {
        var ev1 = NewOutbox();
        var ev2 = NewOutbox(processedAt: Instant.FromUtc(2026, 5, 5, 0, 0));
        var ev3 = NewOutbox(failedPermanently: true);
        var ev4 = NewOutbox();
        await SeedOutbox(ev1, ev2, ev3, ev4);

        var got = await _sut.GetUnprocessedOutboxAsync(limit: 10);
        got.Should().HaveCount(2);
        got.Select(e => e.Id).Should().BeEquivalentTo(new[] { ev1.Id, ev4.Id });
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

        await _sut.SetHoldedDocIdAsync(report.Id, "doc-123", updatedAt);

        var loaded = await _sut.GetByIdAsync(report.Id);
        loaded!.HoldedDocId.Should().Be("doc-123");
        loaded.UpdatedAt.Should().Be(updatedAt);
    }

    private async Task Seed(params ExpenseReport[] reports)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.ExpenseReports.AddRangeAsync(reports);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedOutbox(params HoldedExpenseOutboxEvent[] events)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.HoldedExpenseOutboxEvents.AddRangeAsync(events);
        await ctx.SaveChangesAsync();
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
        bool failedPermanently = false) => new()
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId ?? Guid.NewGuid(),
            EventType = HoldedExpenseOutboxEventType.CreateIncomingDoc,
            OccurredAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            ProcessedAt = processedAt,
            FailedPermanently = failedPermanently
        };
}
