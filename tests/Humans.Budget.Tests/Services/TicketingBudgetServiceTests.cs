using Humans.Budget.Jobs;
using Humans.Testing;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Budget.Contracts;
using Humans.Budget.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Budget.Tests.Services;

/// <summary>
/// Unit tests for <see cref="TicketingBudgetService"/>.
/// Covers the ISO-week bucketing, exclusion of the current (not-yet-complete)
/// week, and delegation of Budget-owned mutations to <see cref="IBudgetManagement"/>.
/// Ticket reads are stubbed via NSubstitute; no DB involvement.
/// </summary>
public class TicketingBudgetServiceTests
{
    private readonly ITicketService _ticketService = Substitute.For<ITicketService>();
    private readonly IBudgetManagement _budgetService = Substitute.For<IBudgetManagement>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 11, 9, 0)); // Wed 11 Mar 2026

    private readonly Guid _yearId = Guid.NewGuid();

    public TicketingBudgetServiceTests()
    {
        _budgetService.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(
            new BudgetYearDetail(_yearId, "2026", "2026", BudgetYearStatus.Active, false, []));
    }

    private TicketingBudgetService CreateSut() =>
        new(_ticketService, _budgetService, _clock, NullLogger<TicketingBudgetService>.Instance);

    [HumansTheory]
    [InlineData("Year")]
    [InlineData("Sync")]
    public async Task TicketingSyncJob_PropagatesShutdownAtEveryBoundary(string boundary)
    {
        using var shutdown = new CancellationTokenSource();
        var logger = new CapturingLogger<TicketingBudgetSyncJob>();
        var bridge = Substitute.For<ITicketingBudgetService>();
        var year = new BudgetYearDetail(_yearId, "2026", "2026", BudgetYearStatus.Active, false, []);
        _budgetService.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            shutdown.Cancel();
            if (string.Equals(boundary, "Year", StringComparison.Ordinal))
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return year;
        });
        bridge.SyncActualsAsync(_yearId, null, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return 1;
        });
        var job = new TicketingBudgetSyncJob(bridge, _budgetService, logger);

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeOfType<OperationCanceledException>().Which.CancellationToken.Should().Be(shutdown.Token);
        _ = _budgetService.Received(1).GetActiveYearAsync(shutdown.Token);
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error
            || entry.Message.Contains("completed", StringComparison.OrdinalIgnoreCase));
        if (string.Equals(boundary, "Year", StringComparison.Ordinal))
            await bridge.DidNotReceiveWithAnyArgs().SyncActualsAsync(default, default);
        else
        {
            await bridge.Received(1).SyncActualsAsync(_yearId, null, shutdown.Token);
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TicketingSyncJob_ReportsGenuineFaults(bool unrelatedCancellation)
    {
        var failure = unrelatedCancellation
            ? (Exception)new OperationCanceledException(new CancellationToken(true))
            : new IOException("Read unavailable");
        var bridge = Substitute.For<ITicketingBudgetService>();
        bridge.SyncActualsAsync(_yearId, null, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var logger = new CapturingLogger<TicketingBudgetSyncJob>();
        var job = new TicketingBudgetSyncJob(bridge, _budgetService, logger);

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync());

        error.Should().BeSameAs(failure);
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Exception == failure);
        logger.Entries.Should().NotContain(entry => entry.Message.Contains("completed", StringComparison.OrdinalIgnoreCase));
    }

    [HumansTheory]
    [InlineData("Year")]
    [InlineData("Orders")]
    [InlineData("Save")]
    public async Task SyncActuals_CancellationStopsAtTheReadOrAtomicSave(string boundary)
    {
        using var shutdown = new CancellationTokenSource();
        var year = new BudgetYearDetail(_yearId, "2026", "2026", BudgetYearStatus.Active, false, []);
        void Read(string name, CancellationToken token)
        {
            if (!string.Equals(name, boundary, StringComparison.Ordinal)) return;
            shutdown.Cancel();
            token.ThrowIfCancellationRequested();
        }
        _budgetService.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("Year", call.Arg<CancellationToken>());
            return year;
        });
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("Orders", call.Arg<CancellationToken>());
            return (IReadOnlyList<TicketOrderInfo>)[BuildOrder(Instant.FromUtc(2026, 3, 2, 8, 0), 100m, 2m, 1m, TicketAttendeeStatus.Valid)];
        });
        _budgetService.SyncTicketingActualsAsync(_yearId, Arg.Any<IReadOnlyList<TicketingWeeklyActuals>>(), null,
            Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("Save", call.Arg<CancellationToken>());
            return 1;
        });
        var logger = new CapturingLogger<TicketingBudgetService>();
        var service = new TicketingBudgetService(_ticketService, _budgetService, _clock, logger);

        var error = await Record.ExceptionAsync(() => service.SyncActualsAsync(_yearId, null, shutdown.Token));

        error.Should().BeOfType<OperationCanceledException>().Which.CancellationToken.Should().Be(shutdown.Token);
        _ = _budgetService.Received(1).GetActiveYearAsync(shutdown.Token);
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        if (!string.Equals(boundary, "Save", StringComparison.Ordinal))
            await _budgetService.DidNotReceiveWithAnyArgs().SyncTicketingActualsAsync(default, default!, default);
        if (!string.Equals(boundary, "Year", StringComparison.Ordinal))
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncActuals_RealFaultsRemainErrors(bool unrelatedCancellation)
    {
        var failure = unrelatedCancellation
            ? (Exception)new OperationCanceledException(new CancellationToken(true))
            : new IOException("Read unavailable");
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var logger = new CapturingLogger<TicketingBudgetService>();
        var service = new TicketingBudgetService(_ticketService, _budgetService, _clock, logger);

        var error = await Record.ExceptionAsync(() => service.SyncActualsAsync(_yearId, null));

        error.Should().BeSameAs(failure);
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Exception == failure);
    }

    [HumansFact]
    public async Task SyncActualsAsync_BucketsByIsoWeek_AndExcludesCurrentWeek()
    {
        // "Now" is Wed 2026-03-11 → current ISO-week Monday is 2026-03-09 (excluded).
        // Two paid orders in the completed week of 2026-03-02 (Mon) .. 2026-03-08 (Sun),
        // one order in the current (incomplete) week → must be excluded.
        var inCompletedWeekA = Instant.FromUtc(2026, 3, 2, 8, 0);  // Mon
        var inCompletedWeekB = Instant.FromUtc(2026, 3, 7, 22, 30); // Sat
        var inCurrentWeek = Instant.FromUtc(2026, 3, 10, 7, 0);  // Tue (current week)

        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(inCompletedWeekA, 100m, 2m, 1m, TicketAttendeeStatus.Valid, TicketAttendeeStatus.CheckedIn),
                BuildOrder(inCompletedWeekB, 50m, 1.5m, 0.5m, TicketAttendeeStatus.Valid),
                BuildOrder(inCurrentWeek, 999m, 10m, 5m,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.CheckedIn,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.Valid),
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(1);

        var sut = CreateSut();

        var result = await sut.SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(1);
        capturedActuals.Should().NotBeNull();
        capturedActuals.Should().HaveCount(1, because: "only the Mar 2–8 week is complete");

        var week = capturedActuals![0];
        week.Monday.Should().Be(new LocalDate(2026, 3, 2));
        week.Sunday.Should().Be(new LocalDate(2026, 3, 8));
        week.TicketCount.Should().Be(3);
        week.Revenue.Should().Be(150m);
        week.StripeFees.Should().Be(3.5m);
        week.TicketTailorFees.Should().Be(1.5m);
    }

    [HumansFact]
    public async Task SyncActualsAsync_BuildsEnglishWeekLabels_RegardlessOfUiCulture()
    {
        // Actuals rows are matched by description, so a non-English operator sync must not
        // produce differently-labelled rows for the same week.
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(Instant.FromUtc(2026, 3, 2, 8, 0), 100m, 2m, 1m, TicketAttendeeStatus.Valid),
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(1);

        string expected;
        using (new CultureScope("en"))
            expected = $"{new LocalDate(2026, 3, 2).ToWeekdayDayMonth()}–{new LocalDate(2026, 3, 8).ToWeekdayDayMonth()}";

        using (new CultureScope("es"))
            await CreateSut().SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().ContainSingle().Which.WeekLabel.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(TicketPaymentStatus.Pending)]
    [InlineData(TicketPaymentStatus.Refunded)]
    [InlineData(TicketPaymentStatus.Cancelled)]
    public async Task SyncActualsAsync_IgnoresNonPaidOrders(TicketPaymentStatus unpaidStatus)
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 3, 10, 0); // inside completed week Mon 2 Mar
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(purchasedAt, 100m, 2m, 1m, TicketAttendeeStatus.Valid),
                BuildOrder(
                    purchasedAt,
                    999m,
                    10m,
                    5m,
                    unpaidStatus,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.CheckedIn),
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(1);

        var sut = CreateSut();

        await sut.SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().NotBeNull().And.ContainSingle();
        capturedActuals![0].TicketCount.Should().Be(1);
        capturedActuals[0].Revenue.Should().Be(100m);
        capturedActuals[0].StripeFees.Should().Be(2m);
        capturedActuals[0].TicketTailorFees.Should().Be(1m);
    }

    [HumansFact]
    public async Task SyncActualsAsync_TreatsNullFeesAsZero()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 3, 10, 0); // inside completed week Mon 2 Mar
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(purchasedAt, 100m, stripeFee: null, applicationFee: null, TicketAttendeeStatus.Valid, TicketAttendeeStatus.CheckedIn),
                BuildOrder(purchasedAt, 50m, stripeFee: 1m, applicationFee: 0.2m, TicketAttendeeStatus.Valid),
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(2);

        var sut = CreateSut();

        await sut.SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().NotBeNull().And.ContainSingle();
        capturedActuals![0].StripeFees.Should().Be(1m);
        capturedActuals[0].TicketTailorFees.Should().Be(0.2m);
        capturedActuals[0].Revenue.Should().Be(150m);
    }

    [HumansFact]
    public async Task SyncActualsAsync_SumsStripeFeeTax_AndTracksFeesOfUnknownTax()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 3, 10, 0); // inside completed week Mon 2 Mar
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(purchasedAt, 100m, stripeFee: 2m, applicationFee: 1m, TicketAttendeeStatus.Valid)
                    with { FeeTax = 0.42m, WithheldTax = 0.10m },
                BuildOrder(purchasedAt, 50m, stripeFee: 1m, applicationFee: 0.5m, TicketAttendeeStatus.Valid),
                BuildOrder(purchasedAt, 50m, stripeFee: 0.7m, applicationFee: 0.5m, TicketAttendeeStatus.Valid)
                    with { FeeTax = 0m },
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(1);

        var sut = CreateSut();

        await sut.SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().NotBeNull().And.ContainSingle();
        capturedActuals![0].StripeFees.Should().Be(3.7m);
        capturedActuals[0].StripeFeeTax.Should().Be(0.42m);
        capturedActuals[0].StripeFeesTaxUnknown.Should().Be(1m, because: "null FeeTax means unknown, not zero");
        capturedActuals[0].StripeFeesUntaxed.Should().Be(0.7m);
    }

    [HumansFact]
    public async Task SyncActualsAsync_ProducesEmptyActuals_WhenNoCompletedWeeks()
    {
        // Only orders inside the current week: nothing should be synced.
        var inCurrentWeek = Instant.FromUtc(2026, 3, 10, 12, 0);
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(inCurrentWeek, 75m, 2m, 0.5m, TicketAttendeeStatus.Valid),
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(0);

        var sut = CreateSut();

        await sut.SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().NotBeNull().And.BeEmpty();
    }

    [HumansFact]
    public async Task SyncActualsAsync_EventWithNoOrdersOfAnyStatus_WritesNothing()
    {
        // Only another event's orders: the year's event has none (moved VendorEventId / missing state).
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(Instant.FromUtc(2026, 3, 3, 10, 0), 100m, 2m, 1m, TicketAttendeeStatus.Valid)
                    with { IsCurrentEvent = false },
            });

        var result = await CreateSut().SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(0);
        await _budgetService.DidNotReceiveWithAnyArgs().SyncTicketingActualsAsync(
            default, default!, default);
    }

    [HumansFact]
    public async Task SyncActualsAsync_AllOrdersRefunded_StillSyncsEmptyActuals()
    {
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(Instant.FromUtc(2026, 3, 3, 10, 0), 100m, 2m, 1m,
                    TicketPaymentStatus.Refunded, TicketAttendeeStatus.Valid),
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(0);

        await CreateSut().SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().NotBeNull().And.BeEmpty();
    }

    [HumansFact]
    public async Task SyncActualsAsync_RefusesNonActiveYear_AndWritesNothing()
    {
        var act = () => CreateSut().SyncActualsAsync(
            Guid.NewGuid(), actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _budgetService.DidNotReceiveWithAnyArgs().SyncTicketingActualsAsync(
            default, default!, default);
    }

    [HumansFact]
    public async Task SyncActualsAsync_ExcludesOrdersFromOtherEvents()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 3, 10, 0);
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(purchasedAt, 100m, 2m, 1m, TicketAttendeeStatus.Valid),
                BuildOrder(purchasedAt, 999m, 10m, 5m, TicketAttendeeStatus.Valid) with { IsCurrentEvent = false },
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(1);

        await CreateSut().SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().ContainSingle().Which.Revenue.Should().Be(100m);
    }

    [HumansFact]
    public async Task SyncActualsAsync_ConfiguredVendorEvent_CountsOnlyThatEventsOrders()
    {
        _budgetService.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(
            new BudgetYearDetail(_yearId, "2026", "2026", BudgetYearStatus.Active, false, [], VendorEventId: "event-2026"));
        var purchasedAt = Instant.FromUtc(2026, 3, 3, 10, 0);
        _ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                BuildOrder(purchasedAt, 100m, 2m, 1m, TicketAttendeeStatus.Valid) with { VendorEventId = "event-2026", IsCurrentEvent = false },
                BuildOrder(purchasedAt, 999m, 10m, 5m, TicketAttendeeStatus.Valid) with { VendorEventId = "event-2025", IsCurrentEvent = true },
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(1);

        await CreateSut().SyncActualsAsync(_yearId, actorUserId: null, Xunit.TestContext.Current.CancellationToken);

        capturedActuals.Should().ContainSingle().Which.Revenue.Should().Be(100m);
    }

    [HumansFact]
    public async Task UpdateProjectionAndRefreshAsync_RequestsOneAtomicParameterAndForecastUpdate()
    {
        var groupId = Guid.NewGuid();
        var yearId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var command = new TicketingProjectionUpdateCommand(
            groupId,
            yearId,
            new LocalDate(2026, 3, 1),
            new LocalDate(2026, 6, 1),
            InitialSalesCount: 10,
            DailySalesRate: 2.5m,
            AverageTicketPrice: 95m,
            VatRate: 21,
            StripeFeePercent: 1.4m,
            StripeFeeFixed: 0.25m,
            TicketTailorFeePercent: 0.5m);
        _budgetService.UpdateTicketingProjectionAsync(groupId, command.StartDate, command.EventDate,
            command.InitialSalesCount, command.DailySalesRate, command.AverageTicketPrice, command.VatRate,
            command.StripeFeePercent, command.StripeFeeFixed, command.TicketTailorFeePercent, actorUserId,
            refreshProjections: true, Xunit.TestContext.Current.CancellationToken).Returns(new TicketingProjectionSaveResult(4));

        _budgetService.GetYearByIdAsync(yearId, Arg.Any<CancellationToken>()).Returns(
            new BudgetYearDetail(yearId, "2026", "Budget", BudgetYearStatus.Active, false,
                [new BudgetGroupDetail(groupId, yearId, "Ticketing", 0, false, false, true, null, [])]));
        var sut = CreateSut();

        var result = await sut.UpdateProjectionAndRefreshAsync(command, actorUserId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(new TicketingProjectionSaveResult(4));
        await _budgetService.Received(1).UpdateTicketingProjectionAsync(groupId, command.StartDate, command.EventDate,
            command.InitialSalesCount, command.DailySalesRate, command.AverageTicketPrice, command.VatRate,
            command.StripeFeePercent, command.StripeFeeFixed, command.TicketTailorFeePercent, actorUserId,
            refreshProjections: true, Xunit.TestContext.Current.CancellationToken);
    }

    private static TicketOrderInfo BuildOrder(
        Instant purchasedAt,
        decimal totalAmount,
        decimal? stripeFee,
        decimal? applicationFee,
        params TicketAttendeeStatus[] attendeeStatuses)
    {
        return BuildOrder(
            purchasedAt,
            totalAmount,
            stripeFee,
            applicationFee,
            TicketPaymentStatus.Paid,
            attendeeStatuses);
    }

    private static TicketOrderInfo BuildOrder(
        Instant purchasedAt,
        decimal totalAmount,
        decimal? stripeFee,
        decimal? applicationFee,
        TicketPaymentStatus paymentStatus,
        params TicketAttendeeStatus[] attendeeStatuses)
    {
        return new TicketOrderInfo(
            Id: Guid.NewGuid(),
            VendorOrderId: $"order-{Guid.NewGuid():N}",
            BuyerName: null,
            BuyerEmail: null,
            TotalAmount: totalAmount,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: paymentStatus,
            VendorEventId: "event",
            PurchasedAt: purchasedAt,
            MatchedUserId: null,
            IsCurrentEvent: true,
            Attendees: attendeeStatuses.Select(status => new TicketAttendeeInfo(
                Id: Guid.NewGuid(),
                VendorTicketId: $"ticket-{Guid.NewGuid():N}",
                AttendeeName: null,
                AttendeeEmail: null,
                TicketTypeName: null,
                Price: 0m,
                Status: status,
                MatchedUserId: null)).ToList(),
            StripeFee: stripeFee,
            ApplicationFee: applicationFee);
    }
}
