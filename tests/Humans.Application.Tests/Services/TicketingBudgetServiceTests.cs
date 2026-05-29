using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using TicketingBudgetService = Humans.Application.Services.Tickets.TicketingBudgetService;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the Application-layer <see cref="TicketingBudgetService"/>.
/// Covers the ISO-week bucketing, exclusion of the current (not-yet-complete)
/// week, and delegation of Budget-owned mutations to <see cref="IBudgetService"/>.
/// Ticket reads are stubbed via NSubstitute; no DB involvement.
/// </summary>
public class TicketingBudgetServiceTests
{
    private readonly ITicketServiceRead _ticketService = Substitute.For<ITicketServiceRead>();
    private readonly IBudgetService _budgetService = Substitute.For<IBudgetService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 11, 9, 0)); // Wed 11 Mar 2026

    private TicketingBudgetService CreateSut() =>
        new(_ticketService, _budgetService, _clock, NullLogger<TicketingBudgetService>.Instance);

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
                Arg.Any<CancellationToken>())
            .Returns(1);

        var sut = CreateSut();

        var result = await sut.SyncActualsAsync(Guid.NewGuid());

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
    public async Task SyncActualsAsync_IgnoresNonPaidOrders()
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
                    TicketPaymentStatus.Pending,
                    TicketAttendeeStatus.Valid,
                    TicketAttendeeStatus.CheckedIn),
            });

        List<TicketingWeeklyActuals>? capturedActuals = null;
        _budgetService.SyncTicketingActualsAsync(
                Arg.Any<Guid>(),
                Arg.Do<IReadOnlyList<TicketingWeeklyActuals>>(a => capturedActuals = a.ToList()),
                Arg.Any<CancellationToken>())
            .Returns(1);

        var sut = CreateSut();

        await sut.SyncActualsAsync(Guid.NewGuid());

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
                Arg.Any<CancellationToken>())
            .Returns(2);

        var sut = CreateSut();

        await sut.SyncActualsAsync(Guid.NewGuid());

        capturedActuals.Should().NotBeNull().And.ContainSingle();
        capturedActuals![0].StripeFees.Should().Be(1m);
        capturedActuals[0].TicketTailorFees.Should().Be(0.2m);
        capturedActuals[0].Revenue.Should().Be(150m);
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
                Arg.Any<CancellationToken>())
            .Returns(0);

        var sut = CreateSut();

        await sut.SyncActualsAsync(Guid.NewGuid());

        capturedActuals.Should().NotBeNull().And.BeEmpty();
    }

    [HumansFact]
    public async Task RefreshProjectionsAsync_DelegatesToBudgetService()
    {
        var yearId = Guid.NewGuid();
        _budgetService.RefreshTicketingProjectionsAsync(yearId, Arg.Any<CancellationToken>())
            .Returns(7);

        var sut = CreateSut();

        var result = await sut.RefreshProjectionsAsync(yearId);

        result.Should().Be(7);
        await _budgetService.Received(1).RefreshTicketingProjectionsAsync(yearId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateProjectionAndRefreshAsync_SavesParametersThenRefreshes()
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
        _budgetService.RefreshTicketingProjectionsAsync(yearId, Arg.Any<CancellationToken>())
            .Returns(4);

        var sut = CreateSut();

        var result = await sut.UpdateProjectionAndRefreshAsync(command, actorUserId);

        result.Should().Be(4);
        await _budgetService.Received(1).UpdateTicketingProjectionAsync(
            groupId,
            command.StartDate,
            command.EventDate,
            command.InitialSalesCount,
            command.DailySalesRate,
            command.AverageTicketPrice,
            command.VatRate,
            command.StripeFeePercent,
            command.StripeFeeFixed,
            command.TicketTailorFeePercent,
            actorUserId);
        await _budgetService.Received(1).RefreshTicketingProjectionsAsync(yearId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetProjectionsAsync_DelegatesToBudgetService()
    {
        var groupId = Guid.NewGuid();
        IReadOnlyList<TicketingWeekProjection> expected =
            new List<TicketingWeekProjection>
            {
                new()
                {
                    WeekLabel = "Apr 6–Apr 12",
                    WeekStart = new LocalDate(2026, 4, 6),
                    WeekEnd = new LocalDate(2026, 4, 12),
                    ProjectedTickets = 10,
                    ProjectedRevenue = 500m,
                    ProjectedStripeFees = 10m,
                    ProjectedTtFees = 5m,
                }
            };
        _budgetService.GetTicketingProjectionEntriesAsync(groupId).Returns(expected);

        var sut = CreateSut();

        var result = await sut.GetProjectionsAsync(groupId);

        result.Should().BeSameAs(expected);
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
