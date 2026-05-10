using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Xunit;

namespace Humans.Application.Tests.Jobs;

public class ExpensePaidPollingJobTests
{
    private readonly IExpenseRepository _repo;
    private readonly IHoldedClient _holdedClient;
    private readonly IExpenseReportService _expenseService;
    private readonly FakeClock _clock;
    private readonly ExpensePaidPollingJob _job;

    private static readonly Instant Now = Instant.FromUtc(2026, 5, 10, 12, 0);

    public ExpensePaidPollingJobTests()
    {
        _repo = Substitute.For<IExpenseRepository>();
        _holdedClient = Substitute.For<IHoldedClient>();
        _expenseService = Substitute.For<IExpenseReportService>();
        _clock = new FakeClock(Now);

        _job = new ExpensePaidPollingJob(
            _repo,
            _holdedClient,
            _expenseService,
            _clock,
            Substitute.For<ILogger<ExpensePaidPollingJob>>());
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static ExpenseReport MakeReport(string holdedDocId = "holded-doc-1") => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = Guid.NewGuid(),
        BudgetCategoryId = Guid.NewGuid(),
        Status = ExpenseReportStatus.SepaSent,
        HoldedDocId = holdedDocId,
        SepaSentAt = Instant.FromUtc(2026, 5, 9, 10, 0),
        CreatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
    };

    private static HoldedPurchaseDocumentDto MakeDoc(
        decimal paymentsPending = 0,
        Instant? approvedAt = null) => new()
        {
            Id = "holded-doc-1",
            DocNumber = "DOC-001",
            Subtotal = 100,
            Tax = 0,
            Total = 100,
            PaymentsTotal = 100,
            PaymentsPending = paymentsPending,
            ApprovedAt = approvedAt ?? Instant.FromUtc(2026, 5, 8, 8, 0),
        };

    // ─── empty queue ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task EmptyQueue_NoClientCalls()
    {
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([]);

        await _job.ExecuteAsync();

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .GetPurchaseDocumentAsync(default!, default);
        await _expenseService.DidNotReceiveWithAnyArgs()
            .MarkPaidAsync(default, default);
    }

    // ─── happy path ───────────────────────────────────────────────────────────

    [HumansFact]
    public async Task HappyPath_PaymentsPending0_AndApprovedAt_CallsMarkPaid()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedClient.GetPurchaseDocumentAsync(report.HoldedDocId!, Arg.Any<CancellationToken>())
            .Returns(MakeDoc(paymentsPending: 0, approvedAt: Instant.FromUtc(2026, 5, 8, 8, 0)));

        await _job.ExecuteAsync();

        await _expenseService.Received(1).MarkPaidAsync(report.Id, Arg.Any<CancellationToken>());
    }

    // ─── no-op conditions ─────────────────────────────────────────────────────

    [HumansFact]
    public async Task PaymentsPendingGreaterThanZero_NoMarkPaid()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedClient.GetPurchaseDocumentAsync(report.HoldedDocId!, Arg.Any<CancellationToken>())
            .Returns(MakeDoc(paymentsPending: 50));

        await _job.ExecuteAsync();

        await _expenseService.DidNotReceiveWithAnyArgs().MarkPaidAsync(default, default);
    }

    [HumansFact]
    public async Task ApprovedAtNull_NoMarkPaid()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedClient.GetPurchaseDocumentAsync(report.HoldedDocId!, Arg.Any<CancellationToken>())
            .Returns(new HoldedPurchaseDocumentDto
            {
                Id = "doc-1",
                DocNumber = "DOC-001",
                Subtotal = 100,
                Tax = 0,
                Total = 100,
                PaymentsTotal = 100,
                PaymentsPending = 0,
                ApprovedAt = null,
            });

        await _job.ExecuteAsync();

        await _expenseService.DidNotReceiveWithAnyArgs().MarkPaidAsync(default, default);
    }

    // ─── error handling ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task Holded404_LogsWarningAndContinues_NextReportStillProcessed()
    {
        var report1 = MakeReport("doc-to-delete");
        var report2 = MakeReport("doc-ok");

        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report1, report2]);

        _holdedClient.GetPurchaseDocumentAsync("doc-to-delete", Arg.Any<CancellationToken>())
            .Throws(new HoldedPermanentException(404, null, "Not Found"));

        _holdedClient.GetPurchaseDocumentAsync("doc-ok", Arg.Any<CancellationToken>())
            .Returns(MakeDoc(paymentsPending: 0, approvedAt: Instant.FromUtc(2026, 5, 8, 8, 0)));

        await _job.ExecuteAsync();

        // report2 should still be marked paid
        await _expenseService.Received(1).MarkPaidAsync(report2.Id, Arg.Any<CancellationToken>());
        // report1 should NOT be marked paid
        await _expenseService.DidNotReceive().MarkPaidAsync(report1.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task TransientException_LogsWarningAndContinues()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedClient.GetPurchaseDocumentAsync(report.HoldedDocId!, Arg.Any<CancellationToken>())
            .Throws(new HoldedTransientException("Gateway timeout"));

        // Should not throw
        var act = () => _job.ExecuteAsync();

        await act.Should().NotThrowAsync();
        await _expenseService.DidNotReceiveWithAnyArgs().MarkPaidAsync(default, default);
    }

    // ─── batch cap ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task MoreThan50Reports_OnlyProcesses50()
    {
        // Create 55 reports with distinct SepaSentAt
        var reports = Enumerable.Range(0, 55)
            .Select(i => new ExpenseReport
            {
                Id = Guid.NewGuid(),
                SubmitterUserId = Guid.NewGuid(),
                BudgetCategoryId = Guid.NewGuid(),
                Status = ExpenseReportStatus.SepaSent,
                HoldedDocId = $"doc-{i}",
                SepaSentAt = Instant.FromUtc(2026, 5, 1, 0, 0) + Duration.FromHours(i),
                CreatedAt = Instant.FromUtc(2026, 4, 1, 9, 0),
            })
            .ToList();

        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns(reports);

        _holdedClient.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new HoldedPurchaseDocumentDto
            {
                Id = (string)callInfo[0],
                DocNumber = "DOC",
                Subtotal = 100,
                Tax = 0,
                Total = 100,
                PaymentsTotal = 100,
                PaymentsPending = 0,
                ApprovedAt = Instant.FromUtc(2026, 5, 8, 8, 0),
            });

        await _job.ExecuteAsync();

        // Only 50 MarkPaid calls, not 55
        await _expenseService.Received(50).MarkPaidAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
