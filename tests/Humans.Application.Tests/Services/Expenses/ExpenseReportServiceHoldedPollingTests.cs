using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Application.Tests.Services.Expenses;

public class ExpenseReportServiceHoldedPollingTests
{
    private const int BatchSize = 50;

    private readonly IExpenseRepository _repo;
    private readonly IHoldedClient _holdedClient;
    private readonly FakeClock _clock;
    private readonly ExpenseReportService _sut;

    private static readonly Instant Now = Instant.FromUtc(2026, 5, 10, 12, 0);

    public ExpenseReportServiceHoldedPollingTests()
    {
        _repo = Substitute.For<IExpenseRepository>();
        _holdedClient = Substitute.For<IHoldedClient>();
        _clock = new FakeClock(Now);

        // MarkPaidAsync flows through the service to _repo.MarkPaidAsync — return true so
        // the audit-log call succeeds.
        _repo.MarkPaidAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _sut = new ExpenseReportService(
            _repo,
            Substitute.For<IFileStorage>(),
            Substitute.For<IBudgetService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<IUserService>(),
            Substitute.For<IProfileService>(),
            Substitute.For<IAuditLogService>(),
            _holdedClient,
            _clock,
            Substitute.For<ILogger<ExpenseReportService>>());
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static ExpenseReportDto MakeReport(string holdedDocId = "holded-doc-1") => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = Guid.NewGuid(),
        BudgetCategoryId = Guid.NewGuid(),
        BudgetYearId = Guid.NewGuid(),
        Status = ExpenseReportStatus.SepaSent,
        PayeeName = "",
        PayeeIban = "",
        Total = 0m,
        HoldedDocId = holdedDocId,
        SepaSentAt = Instant.FromUtc(2026, 5, 9, 10, 0),
        CreatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
        UpdatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
        Lines = [],
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

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .GetPurchaseDocumentAsync(default!, default);
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkPaidAsync(default, default, default);
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

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _repo.Received(1).MarkPaidAsync(report.Id, Now, Arg.Any<CancellationToken>());
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

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _repo.DidNotReceiveWithAnyArgs().MarkPaidAsync(default, default, default);
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

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _repo.DidNotReceiveWithAnyArgs().MarkPaidAsync(default, default, default);
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

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        // report2 should still be marked paid
        await _repo.Received(1).MarkPaidAsync(report2.Id, Now, Arg.Any<CancellationToken>());
        // report1 should NOT be marked paid
        await _repo.DidNotReceive().MarkPaidAsync(report1.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
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
        var act = () => _sut.PollHoldedPaidStatusAsync(BatchSize);

        await act.Should().NotThrowAsync();
        await _repo.DidNotReceiveWithAnyArgs().MarkPaidAsync(default, default, default);
    }

    // ─── batch cap ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task MoreThan50Reports_OnlyProcesses50()
    {
        // Create 55 reports with distinct SepaSentAt
        var reports = Enumerable.Range(0, 55)
            .Select(i => new ExpenseReportDto
            {
                Id = Guid.NewGuid(),
                SubmitterUserId = Guid.NewGuid(),
                BudgetCategoryId = Guid.NewGuid(),
                BudgetYearId = Guid.NewGuid(),
                Status = ExpenseReportStatus.SepaSent,
                PayeeName = "",
                PayeeIban = "",
                Total = 0m,
                HoldedDocId = $"doc-{i}",
                SepaSentAt = Instant.FromUtc(2026, 5, 1, 0, 0) + Duration.FromHours(i),
                CreatedAt = Instant.FromUtc(2026, 4, 1, 9, 0),
                UpdatedAt = Instant.FromUtc(2026, 4, 1, 9, 0),
                Lines = [],
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

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        // Only 50 MarkPaid calls, not 55
        await _repo.Received(50).MarkPaidAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }
}
