using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Application.Services.Finance.Dtos;
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
    private readonly IHoldedFinanceService _holdedFinance;
    private readonly FakeClock _clock;
    private readonly ExpenseReportService _sut;

    private static readonly Instant Now = Instant.FromUtc(2026, 5, 10, 12, 0);

    public ExpenseReportServiceHoldedPollingTests()
    {
        _repo = Substitute.For<IExpenseRepository>();
        _holdedClient = Substitute.For<IHoldedClient>();
        _holdedFinance = Substitute.For<IHoldedFinanceService>();
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
            Substitute.For<IAuditLogService>(),
            _holdedClient,
            _holdedFinance,
            _clock,
            Substitute.For<ILogger<ExpenseReportService>>());
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static ExpenseReportDto MakeReport(
        string? holdedContactId = "contact-1",
        int? supplierAccountNum = 40000007,
        string holdedDocId = "holded-doc-1") => new()
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
            HoldedContactId = holdedContactId,
            HoldedSupplierAccountNum = supplierAccountNum,
            SepaSentAt = Instant.FromUtc(2026, 5, 9, 10, 0),
            CreatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
            Lines = [],
        };

    private static HoldedCreditorStatus MakeSettledStatus(LocalDate? lastPaymentDate = null) =>
        new(40000007, Balance: 0m, OwedToMember: 0m,
            LastPaymentDate: lastPaymentDate, TotalPaid: 121m);

    private static HoldedCreditorStatus MakeOwedStatus() =>
        new(40000007, Balance: -50m, OwedToMember: 50m,
            LastPaymentDate: null, TotalPaid: 0m);

    // ─── empty queue ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task EmptyQueue_NoClientCalls()
    {
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _holdedClient.DidNotReceiveWithAnyArgs()
            .GetPurchaseDocumentAsync(null!, CancellationToken.None);
        await _repo.DidNotReceiveWithAnyArgs()
            .MarkPaidAsync(Guid.Empty, default, CancellationToken.None);
    }

    // ─── happy path ───────────────────────────────────────────────────────────

    [HumansFact]
    public async Task HappyPath_BalanceZero_WithLastPaymentDate_CallsMarkPaidWithDate()
    {
        var paymentDate = new LocalDate(2026, 5, 8);
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-1", Arg.Any<CancellationToken>())
            .Returns(MakeSettledStatus(paymentDate));

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        var expectedInstant = paymentDate.AtStartOfDayInZone(
            DateTimeZoneProviders.Tzdb["Europe/Madrid"]).ToInstant();
        await _repo.Received(1).MarkPaidAsync(report.Id, expectedInstant, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task HappyPath_BalanceZero_NoLastPaymentDate_CallsMarkPaidWithNow()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-1", Arg.Any<CancellationToken>())
            .Returns(MakeSettledStatus(lastPaymentDate: null));

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _repo.Received(1).MarkPaidAsync(report.Id, Now, Arg.Any<CancellationToken>());
    }

    // ─── no-op conditions ─────────────────────────────────────────────────────

    [HumansFact]
    public async Task BalanceNegative_NoMarkPaid()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-1", Arg.Any<CancellationToken>())
            .Returns(MakeOwedStatus());

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _repo.DidNotReceiveWithAnyArgs().MarkPaidAsync(Guid.Empty, default, CancellationToken.None);
    }

    [HumansFact]
    public async Task NoHoldedContactId_SkipsReport_NoFinanceCall()
    {
        var report = MakeReport(holdedContactId: null);
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _holdedFinance.DidNotReceiveWithAnyArgs()
            .GetCreditorStatusAsync(default, null!, CancellationToken.None);
        await _repo.DidNotReceiveWithAnyArgs().MarkPaidAsync(Guid.Empty, default, CancellationToken.None);
    }

    [HumansFact]
    public async Task CreditorStatusNull_NoMarkPaid()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-1", Arg.Any<CancellationToken>())
            .Returns((HoldedCreditorStatus?)null);

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _repo.DidNotReceiveWithAnyArgs().MarkPaidAsync(Guid.Empty, default, CancellationToken.None);
    }

    // ─── account-num backfill ─────────────────────────────────────────────────

    [HumansFact]
    public async Task MissingAccountNum_BackfillsFromContact_ThenPollsFinance()
    {
        var report = MakeReport(supplierAccountNum: null);
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedClient.GetContactAsync("contact-1", Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "contact-1", SupplierAccountNum = 40000007 });
        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-1", Arg.Any<CancellationToken>())
            .Returns(MakeSettledStatus(new LocalDate(2026, 5, 8)));

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        await _repo.Received(1).SetHoldedContactLinkAsync(
            report.Id, "contact-1", 40000007, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkPaidAsync(report.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    // ─── error handling ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task Holded404_LogsWarningAndContinues_NextReportStillProcessed()
    {
        var report1 = MakeReport(holdedContactId: "contact-gone");
        var report2 = MakeReport(holdedContactId: "contact-ok");

        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report1, report2]);

        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-gone", Arg.Any<CancellationToken>())
            .Throws(new HoldedPermanentException(404, null, "Not Found"));

        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-ok", Arg.Any<CancellationToken>())
            .Returns(MakeSettledStatus(new LocalDate(2026, 5, 8)));

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        // report2 should still be marked paid
        await _repo.Received(1).MarkPaidAsync(report2.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        // report1 should NOT be marked paid
        await _repo.DidNotReceive().MarkPaidAsync(report1.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task TransientException_LogsWarningAndContinues()
    {
        var report = MakeReport();
        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns([report]);
        _holdedFinance.GetCreditorStatusAsync(40000007, "contact-1", Arg.Any<CancellationToken>())
            .Throws(new HoldedTransientException("Gateway timeout"));

        // Should not throw
        var act = () => _sut.PollHoldedPaidStatusAsync(BatchSize);

        await act.Should().NotThrowAsync();
        await _repo.DidNotReceiveWithAnyArgs().MarkPaidAsync(Guid.Empty, default, CancellationToken.None);
    }

    // ─── batch cap ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task MoreThan50Reports_OnlyProcesses50()
    {
        // Create 55 reports with distinct SepaSentAt, all with contact links
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
                HoldedContactId = $"contact-{i}",
                HoldedSupplierAccountNum = 40000000 + i,
                SepaSentAt = Instant.FromUtc(2026, 5, 1, 0, 0) + Duration.FromHours(i),
                CreatedAt = Instant.FromUtc(2026, 4, 1, 9, 0),
                UpdatedAt = Instant.FromUtc(2026, 4, 1, 9, 0),
                Lines = [],
            })
            .ToList();

        _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, Arg.Any<CancellationToken>())
            .Returns(reports);

        _holdedFinance.GetCreditorStatusAsync(Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new HoldedCreditorStatus(
                (int?)callInfo[0], Balance: 0m, OwedToMember: 0m,
                LastPaymentDate: new LocalDate(2026, 5, 8), TotalPaid: 100m));

        await _sut.PollHoldedPaidStatusAsync(BatchSize);

        // Only 50 MarkPaid calls, not 55
        await _repo.Received(50).MarkPaidAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }
}
