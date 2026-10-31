using Xunit;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Expenses.Tests.Services;

/// <summary>
/// The production duplicate-document incident: when a push loses the line's doc-id write, the
/// stored document wins and the audit summary names the orphan. Runs the real repository over a
/// shared in-memory store. The one-drain-at-a-time gate is covered in
/// <c>ExpenseReportServiceHoldedOutboxTests</c>.
/// </summary>
// The drain gate is static (one drain per process), so classes that drain run serially.
[Collection("HoldedDrainGate")]
public class HoldedOutboxDuplicateGuardTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 10, 12, 0);

    private readonly ExpenseRepository _repo;
    private readonly IHoldedClient _holded = Substitute.For<IHoldedClient>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly Guid _reportId = Guid.NewGuid();
    private readonly Guid _lineId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();

    public HoldedOutboxDuplicateGuardTests()
    {
        var options = new DbContextOptionsBuilder<ExpensesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new ExpenseRepository(new TestDbContextFactory<ExpensesDbContext>(options));

        _holded.IsConfigured.Returns(true);
        _holded.GetContactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "contact-1", SupplierAccountNum = 40000050 });
        _holded.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldedPurchaseDocumentDto
            {
                ApprovedAt = null,
            });

        using var ctx = new ExpensesDbContext(options);
        ctx.ExpenseReports.Add(new ExpenseReport
        {
            Id = _reportId,
            SubmitterUserId = Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = ExpenseReportStatus.Approved,
            PayeeName = "Alice Smith",
            Total = 50m,
            CreatedAt = Now,
            UpdatedAt = Now,
            Lines = [new ExpenseLine
            {
                Id = _lineId, Description = "Wood", Amount = 50m, LineType = ExpenseLineType.Receipt,
            }],
        });
        ctx.HoldedExpenseOutboxEvents.Add(new HoldedExpenseOutboxEvent
        {
            Id = _eventId,
            ExpenseReportId = _reportId,
            EventType = HoldedExpenseOutboxEventType.CreateIncomingDoc,
            OccurredAt = Now,
        });
        ctx.SaveChanges();
    }

    private ExpenseReportBackgroundProcessor NewProcessor()
    {
        var writes = Substitute.For<IHoldedFinanceWrite>();
        writes.EnsureCreditorContactAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("contact-1");
        return new ExpenseReportBackgroundProcessor(
            _repo, Substitute.For<IFileStorage>(), Substitute.For<IUserService>(), _holded,
            Substitute.For<IHoldedFinanceService>(), writes, _audit, new FakeClock(Now),
            NullLogger<ExpenseReportBackgroundProcessor>.Instance);
    }

    [HumansFact]
    public async Task LostDocIdRace_KeepsTheStoredDocument_AndTheAuditSummaryNamesTheOrphan()
    {
        // The report snapshot the push works from shows no doc id; "doc-A" lands between that
        // snapshot and this run's create. Nothing is deleted in Holded: the audit entry names the
        // extra document so it can be removed by hand.
        var ct = TestContext.Current.CancellationToken;
        _holded.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                (await _repo.SetLineHoldedDocIdAsync(_lineId, "doc-A", Now, ct)).Should().BeTrue();
                return "doc-B";
            });

        await NewProcessor().DrainHoldedOutboxAsync(10, ct);

        await _holded.Received(1).ApprovePurchaseDocumentAsync("doc-A", Arg.Any<CancellationToken>());
        await _holded.DidNotReceive().ApprovePurchaseDocumentAsync("doc-B", Arg.Any<CancellationToken>());
        (await _repo.GetByIdAsync(_reportId, ct))!.Lines.Single().HoldedDocId.Should().Be("doc-A");
        (await _repo.GetUnprocessedOutboxAsync(Now + Duration.FromDays(1), 10, ct)).Should().BeEmpty();
        await _audit.Received(1).LogAsync(
            ExpensesAuditActions.ExpenseHoldedPushed, Arg.Any<string>(), _reportId,
            Arg.Is<string>(d => d.Contains("doc-A", StringComparison.Ordinal)
                && d.Contains("orphan purchase document doc-B", StringComparison.Ordinal)
                && d.Contains("manual removal in Holded", StringComparison.Ordinal)),
            Arg.Any<string>());
    }
}
