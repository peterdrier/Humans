using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using TicketingBudgetService = Humans.Application.Services.Tickets.TicketingBudgetService;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the Application-layer <see cref="TicketingBudgetService"/>.
/// Covers the ISO-week bucketing, exclusion of the current (not-yet-complete)
/// week, and delegation of Budget-owned mutations to <see cref="IBudgetService"/>.
/// Repository reads are stubbed via NSubstitute; no DB involvement.
/// </summary>
public class TicketingBudgetServiceTests
{
    private readonly ITicketingBudgetRepository _repo = Substitute.For<ITicketingBudgetRepository>();
    private readonly IBudgetService _budgetService = Substitute.For<IBudgetService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 11, 9, 0)); // Wed 11 Mar 2026

    private TicketingBudgetService CreateSut() =>
        new(_repo, _budgetService, _clock, NullLogger<TicketingBudgetService>.Instance);

    [Fact]
    public async Task SyncActualsAsync_BucketsByIsoWeek_AndExcludesCurrentWeek()
    {
        // "Now" is Wed 2026-03-11 → current ISO-week Monday is 2026-03-09 (excluded).
        // Two paid orders in the completed week of 2026-03-02 (Mon) .. 2026-03-08 (Sun),
        // one order in the current (incomplete) week → must be excluded.
        var inCompletedWeekA = Instant.FromUtc(2026, 3, 2, 8, 0);  // Mon
        var inCompletedWeekB = Instant.FromUtc(2026, 3, 7, 22, 30); // Sat
        var inCurrentWeek = Instant.FromUtc(2026, 3, 10, 7, 0);  // Tue (current week)

        _repo.GetPaidOrderSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PaidTicketOrderSummary>
            {
                new(inCompletedWeekA, 100m, 2m, 1m, TicketCount: 2),
                new(inCompletedWeekB, 50m, 1.5m, 0.5m, TicketCount: 1),
                new(inCurrentWeek, 999m, 10m, 5m, TicketCount: 10),
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

    [Fact]
    public async Task SyncActualsAsync_TreatsNullFeesAsZero()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 3, 10, 0); // inside completed week Mon 2 Mar
        _repo.GetPaidOrderSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PaidTicketOrderSummary>
            {
                new(purchasedAt, 100m, StripeFee: null, ApplicationFee: null, TicketCount: 2),
                new(purchasedAt,  50m, StripeFee: 1m,   ApplicationFee: 0.2m, TicketCount: 1),
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

    [Fact]
    public async Task SyncActualsAsync_ProducesEmptyActuals_WhenNoCompletedWeeks()
    {
        // Only orders inside the current week: nothing should be synced.
        var inCurrentWeek = Instant.FromUtc(2026, 3, 10, 12, 0);
        _repo.GetPaidOrderSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PaidTicketOrderSummary>
            {
                new(inCurrentWeek, 75m, 2m, 0.5m, TicketCount: 1),
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public void GetActualTicketsSold_DelegatesToBudgetService()
    {
        var group = new BudgetGroup { Id = Guid.NewGuid(), Name = "Ticketing" };
        _budgetService.GetActualTicketsSold(group).Returns(187);

        var sut = CreateSut();

        sut.GetActualTicketsSold(group).Should().Be(187);
    }
}
