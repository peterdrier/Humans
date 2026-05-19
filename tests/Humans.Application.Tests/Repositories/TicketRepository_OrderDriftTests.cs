using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Tickets;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Repositories;

public sealed class TicketRepository_OrderDriftTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly TicketRepository _repo;

    public TicketRepository_OrderDriftTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new TicketRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [HumansFact]
    public async Task GetOrderDrift_ReturnsOrdersWhereValidLessThanIssued()
    {
        var now = _clock.GetCurrentInstant();

        // "clean" order: 2 attendees, both Valid → not in result
        var cleanOrderId = Guid.NewGuid();
        _dbContext.TicketOrders.Add(new TicketOrder
        {
            Id = cleanOrderId,
            VendorOrderId = "ord_clean",
            BuyerName = "Clean Buyer",
            BuyerEmail = "clean@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_1",
            PurchasedAt = now,
            SyncedAt = now,
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_c1",
            TicketOrderId = cleanOrderId,
            AttendeeName = "A1",
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = "ev_1",
            SyncedAt = now,
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_c2",
            TicketOrderId = cleanOrderId,
            AttendeeName = "A2",
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = "ev_1",
            SyncedAt = now,
        });

        // "drift" order: 3 attendees, 2 Valid + 1 Void → in result, IssuedCount=3, ValidCount=2
        var driftOrderId = Guid.NewGuid();
        _dbContext.TicketOrders.Add(new TicketOrder
        {
            Id = driftOrderId,
            VendorOrderId = "ord_drift",
            BuyerName = "Drift Buyer",
            BuyerEmail = "drift@example.com",
            TotalAmount = 150m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_1",
            PurchasedAt = now,
            SyncedAt = now,
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_d1",
            TicketOrderId = driftOrderId,
            AttendeeName = "B1",
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = "ev_1",
            SyncedAt = now,
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_d2",
            TicketOrderId = driftOrderId,
            AttendeeName = "B2",
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = "ev_1",
            SyncedAt = now,
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_d3",
            TicketOrderId = driftOrderId,
            AttendeeName = "B3",
            Status = TicketAttendeeStatus.Void,
            VendorEventId = "ev_1",
            SyncedAt = now,
        });

        // "refunded" order: 2 attendees, 1 Valid + 1 Void, PaymentStatus=Refunded → not in result
        var refundedOrderId = Guid.NewGuid();
        _dbContext.TicketOrders.Add(new TicketOrder
        {
            Id = refundedOrderId,
            VendorOrderId = "ord_refunded",
            BuyerName = "Refunded Buyer",
            BuyerEmail = "refund@example.com",
            TotalAmount = 50m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Refunded,
            VendorEventId = "ev_1",
            PurchasedAt = now,
            SyncedAt = now,
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_r1",
            TicketOrderId = refundedOrderId,
            AttendeeName = "C1",
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = "ev_1",
            SyncedAt = now,
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_r2",
            TicketOrderId = refundedOrderId,
            AttendeeName = "C2",
            Status = TicketAttendeeStatus.Void,
            VendorEventId = "ev_1",
            SyncedAt = now,
        });

        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetOrderDriftAsync();

        result.Should().HaveCount(1);
        result[0].OrderId.Should().Be(driftOrderId);
        result[0].IssuedCount.Should().Be(3);
        result[0].ValidCount.Should().Be(2);
    }
}
