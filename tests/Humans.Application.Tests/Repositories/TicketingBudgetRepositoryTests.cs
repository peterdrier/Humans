using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Tickets;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories;

public sealed class TicketingBudgetRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly TicketingBudgetRepository _repo;

    public TicketingBudgetRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new TicketingBudgetRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<TicketOrder> SeedOrderAsync(
        TicketPaymentStatus status,
        decimal total,
        decimal? stripeFee,
        decimal? applicationFee,
        Instant purchasedAt,
        TicketAttendeeStatus[] attendeeStatuses)
    {
        var order = new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = $"vo-{Guid.NewGuid():N}",
            PaymentStatus = status,
            TotalAmount = total,
            StripeFee = stripeFee,
            ApplicationFee = applicationFee,
            PurchasedAt = purchasedAt,
            SyncedAt = purchasedAt,
        };
        foreach (var s in attendeeStatuses)
        {
            order.Attendees.Add(new TicketAttendee
            {
                Id = Guid.NewGuid(),
                VendorTicketId = $"vt-{Guid.NewGuid():N}",
                TicketOrderId = order.Id,
                Status = s,
                SyncedAt = purchasedAt,
            });
        }
        _dbContext.TicketOrders.Add(order);
        await _dbContext.SaveChangesAsync();
        return order;
    }

    // ==========================================================================
    // GetPaidOrderSummariesAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetPaidOrderSummariesAsync_ReturnsEmpty_WhenNoOrders()
    {
        var result = await _repo.GetPaidOrderSummariesAsync();
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetPaidOrderSummariesAsync_ExcludesNonPaidOrders()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 1, 12, 0);
        await SeedOrderAsync(TicketPaymentStatus.Paid, 50m, 1.5m, 0.5m, purchasedAt,
            new[] { TicketAttendeeStatus.Valid });
        await SeedOrderAsync(TicketPaymentStatus.Pending, 80m, 2m, 0.8m, purchasedAt,
            new[] { TicketAttendeeStatus.Valid });
        await SeedOrderAsync(TicketPaymentStatus.Refunded, 90m, 2m, 0.9m, purchasedAt,
            new[] { TicketAttendeeStatus.Valid });
        await SeedOrderAsync(TicketPaymentStatus.Cancelled, 100m, 0m, 0m, purchasedAt,
            new[] { TicketAttendeeStatus.Void });

        var result = await _repo.GetPaidOrderSummariesAsync();

        result.Should().HaveCount(1);
        result[0].TotalAmount.Should().Be(50m);
    }

    [HumansFact]
    public async Task GetPaidOrderSummariesAsync_CountsOnlyValidAndCheckedInAttendees()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 1, 12, 0);
        await SeedOrderAsync(TicketPaymentStatus.Paid, 200m, 6m, 2m, purchasedAt, new[]
        {
            TicketAttendeeStatus.Valid,      // counted
            TicketAttendeeStatus.CheckedIn,  // counted
            TicketAttendeeStatus.Void,       // excluded
            TicketAttendeeStatus.Valid,      // counted
            TicketAttendeeStatus.Void,       // excluded
        });

        var result = await _repo.GetPaidOrderSummariesAsync();

        result.Should().ContainSingle();
        result[0].TicketCount.Should().Be(3);
    }

    [HumansFact]
    public async Task GetPaidOrderSummariesAsync_ReturnsAllFieldsIncludingNullableFees()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 5, 10, 30);
        await SeedOrderAsync(TicketPaymentStatus.Paid, 150m, stripeFee: null, applicationFee: null, purchasedAt,
            new[] { TicketAttendeeStatus.Valid });

        var result = await _repo.GetPaidOrderSummariesAsync();

        result.Should().ContainSingle();
        var summary = result[0];
        summary.PurchasedAt.Should().Be(purchasedAt);
        summary.TotalAmount.Should().Be(150m);
        summary.StripeFee.Should().BeNull();
        summary.ApplicationFee.Should().BeNull();
        summary.TicketCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetPaidOrderSummariesAsync_ReturnsZeroTicketCount_WhenAllAttendeesVoid()
    {
        var purchasedAt = Instant.FromUtc(2026, 3, 1, 12, 0);
        await SeedOrderAsync(TicketPaymentStatus.Paid, 50m, 1m, 0.3m, purchasedAt,
            new[] { TicketAttendeeStatus.Void, TicketAttendeeStatus.Void });

        var result = await _repo.GetPaidOrderSummariesAsync();

        result.Should().ContainSingle();
        result[0].TicketCount.Should().Be(0);
    }
}
