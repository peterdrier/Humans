using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TicketQueryServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly TicketQueryService _service;

    public TicketQueryServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _service = new TicketQueryService(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetSalesAggregatesAsync_ExcludesVoidTicketsFromCountsAndVipDonations()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_weekly",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 500m,
            DonationAmount = 25m,
            VatAmount = 28.64m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 5),
            Attendees =
            [
                new TicketAttendee
                {
                    Id = Guid.NewGuid(),
                    VendorTicketId = "tkt_valid_vip",
                    TicketOrderId = orderId,
                    TicketOrder = null!,
                    AttendeeName = "Valid VIP",
                    TicketTypeName = "VIP",
                    Price = 400m,
                    Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 5),
                },
                new TicketAttendee
                {
                    Id = Guid.NewGuid(),
                    VendorTicketId = "tkt_void_vip",
                    TicketOrderId = orderId,
                    TicketOrder = null!,
                    AttendeeName = "Void VIP",
                    TicketTypeName = "VIP",
                    Price = 500m,
                    Status = TicketAttendeeStatus.Void,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 5),
                }
            ]
        };

        _dbContext.TicketOrders.Add(order);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetSalesAggregatesAsync();

        result.WeeklySales.Should().ContainSingle();
        result.QuarterlySales.Should().ContainSingle();

        var weekly = result.WeeklySales.Single();
        weekly.TicketsSold.Should().Be(1);
        weekly.Donations.Should().Be(25m);
        weekly.VipDonations.Should().Be(400m - TicketConstants.VipThresholdEuros);

        var quarterly = result.QuarterlySales.Single();
        quarterly.TicketsSold.Should().Be(1);
        quarterly.Donations.Should().Be(25m);
        quarterly.VipDonations.Should().Be(400m - TicketConstants.VipThresholdEuros);
    }

    [Fact]
    public async Task GetSalesAggregatesAsync_ExcludesRefundedAndCancelledOrders()
    {
        await _dbContext.TicketOrders.AddRangeAsync(
            MakeOrder("ord_paid", TicketPaymentStatus.Paid, Instant.FromUtc(2026, 3, 2, 10, 0), 100m, 0m, 9.09m, 1, 0m),
            MakeOrder("ord_refunded", TicketPaymentStatus.Refunded, Instant.FromUtc(2026, 3, 2, 12, 0), 999m, 50m, 90m, 1, 200m),
            MakeOrder("ord_cancelled", TicketPaymentStatus.Cancelled, Instant.FromUtc(2026, 3, 3, 12, 0), 888m, 25m, 80m, 1, 100m));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetSalesAggregatesAsync();

        result.WeeklySales.Should().ContainSingle();
        var weekly = result.WeeklySales.Single();
        weekly.OrderCount.Should().Be(1);
        weekly.GrossRevenue.Should().Be(100m);
        weekly.Donations.Should().Be(0m);
        weekly.VatAmount.Should().Be(9.09m);
        weekly.TicketsSold.Should().Be(1);
        weekly.VipDonations.Should().Be(0m);
    }

    private static TicketOrder MakeOrder(
        string vendorOrderId,
        TicketPaymentStatus paymentStatus,
        Instant purchasedAt,
        decimal totalAmount,
        decimal donationAmount,
        decimal vatAmount,
        int ticketCount,
        decimal vipPremiumPerTicket)
    {
        var orderId = Guid.NewGuid();
        var attendees = Enumerable.Range(1, ticketCount)
            .Select(i => new TicketAttendee
            {
                Id = Guid.NewGuid(),
                VendorTicketId = $"{vendorOrderId}_tkt_{i}",
                TicketOrderId = orderId,
                TicketOrder = null!,
                AttendeeName = $"Attendee {i}",
                TicketTypeName = vipPremiumPerTicket > 0 ? "VIP" : "Full Week",
                Price = TicketConstants.VipThresholdEuros + vipPremiumPerTicket,
                Status = TicketAttendeeStatus.Valid,
                VendorEventId = "ev_test",
                SyncedAt = purchasedAt,
            })
            .ToList();

        return new TicketOrder
        {
            Id = orderId,
            VendorOrderId = vendorOrderId,
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = totalAmount,
            DonationAmount = donationAmount,
            VatAmount = vatAmount,
            Currency = "EUR",
            PaymentStatus = paymentStatus,
            VendorEventId = "ev_test",
            PurchasedAt = purchasedAt,
            SyncedAt = purchasedAt,
            Attendees = attendees
        };
    }
}
