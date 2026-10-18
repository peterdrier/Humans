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
/// The production duplicate-document incident: overlapping drains over one outbox event each
/// created their own Holded document. Runs the real repository over a shared in-memory store.
/// </summary>
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
                Id = "doc", DocNumber = "", Subtotal = 0m, Tax = 0m, Total = 0m,
                PaymentsTotal = 0m, PaymentsPending = 0m, ApprovedAt = null,
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
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("contact-1");
        return new ExpenseReportBackgroundProcessor(
            _repo, Substitute.For<IFileStorage>(), Substitute.For<IUserService>(), _holded,
            Substitute.For<IHoldedFinanceService>(), writes, _audit, new FakeClock(Now),
            NullLogger<ExpenseReportBackgroundProcessor>.Instance);
    }

    [HumansFact]
    public async Task TwoOverlappingDrains_CreateOneDocument_AndLeaveOneHoldedDocId()
    {
        // Drain 1 parks inside the Holded create call; drain 2 runs to completion meanwhile.
        // On InMemory the claim fallback is load-check-save, not atomic, so this proves the claim
        // is consulted before processing; atomicity on Postgres comes from the single conditional
        // UPDATE in TryClaimOutboxAsync.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holded.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return "doc-A";
            });

        var drain1 = NewProcessor().DrainHoldedOutboxAsync(10, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await NewProcessor().DrainHoldedOutboxAsync(10, ct);
        gate.SetResult();
        await drain1;

        await _holded.Received(1).CreatePurchaseDocumentAsync(
            Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>());
        await _holded.DidNotReceiveWithAnyArgs().DeletePurchaseDocumentAsync(null!, default);
        var report = await _repo.GetByIdAsync(_reportId, ct);
        report!.Lines.Single().HoldedDocId.Should().Be("doc-A");
        (await _repo.GetUnprocessedOutboxAsync(Now + Duration.FromDays(1), 10, ct)).Should().BeEmpty();
        await _audit.Received(1).LogAsync(
            AuditAction.ExpenseHoldedPushed, Arg.Any<string>(), _reportId,
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task LostDocIdRace_DeletesTheExtraDocument_AndContinuesWithTheStoredOne()
    {
        // The report snapshot the push works from shows no doc id; "doc-A" lands between that
        // snapshot and this run's create (a concurrent push that got past the claim).
        var ct = Xunit.TestContext.Current.CancellationToken;
        _holded.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                (await _repo.SetLineHoldedDocIdAsync(_lineId, "doc-A", Now, ct)).Should().BeTrue();
                return "doc-B";
            });

        await NewProcessor().DrainHoldedOutboxAsync(10, ct);

        await _holded.Received(1).DeletePurchaseDocumentAsync("doc-B", Arg.Any<CancellationToken>());
        await _holded.DidNotReceive().DeletePurchaseDocumentAsync("doc-A", Arg.Any<CancellationToken>());
        await _holded.Received(1).ApprovePurchaseDocumentAsync("doc-A", Arg.Any<CancellationToken>());
        await _holded.DidNotReceive().ApprovePurchaseDocumentAsync("doc-B", Arg.Any<CancellationToken>());
        (await _repo.GetByIdAsync(_reportId, ct))!.Lines.Single().HoldedDocId.Should().Be("doc-A");
        await _audit.Received(1).LogAsync(
            AuditAction.ExpenseHoldedPushed, Arg.Any<string>(), _reportId,
            Arg.Is<string>(d => d.Contains("doc-A", StringComparison.Ordinal)
                && d.Contains("duplicate purchase document doc-B deleted", StringComparison.Ordinal)),
            Arg.Any<string>());
    }
}
