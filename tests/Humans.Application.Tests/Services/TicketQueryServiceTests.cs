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

    // ====================================================================
    // GetOrdersPageAsync tests
    // ====================================================================

    [Fact]
    public async Task GetOrdersPageAsync_ReturnsPagedResults()
    {
        for (var i = 0; i < 5; i++)
        {
            _dbContext.TicketOrders.Add(MakeOrder(
                $"ord_{i}", TicketPaymentStatus.Paid,
                Instant.FromUtc(2026, 3, 1 + i, 10, 0),
                100m, 0m, 9.09m, 1, 0m));
        }

        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrdersPageAsync(
            null, "date", true, 1, 2, null, null, null);

        result.TotalCount.Should().Be(5);
        result.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetOrdersPageAsync_FiltersbyPaymentStatus()
    {
        _dbContext.TicketOrders.Add(MakeOrder("ord_paid", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 100m, 0m, 9.09m, 1, 0m));
        _dbContext.TicketOrders.Add(MakeOrder("ord_refund", TicketPaymentStatus.Refunded,
            Instant.FromUtc(2026, 3, 2, 10, 0), 200m, 0m, 0m, 1, 0m));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrdersPageAsync(
            null, "date", true, 1, 25, "Paid", null, null);

        result.TotalCount.Should().Be(1);
        result.Rows.Single().VendorOrderId.Should().Be("ord_paid");
    }

    [Fact]
    public async Task GetOrdersPageAsync_SortsByAmount()
    {
        _dbContext.TicketOrders.Add(MakeOrder("ord_cheap", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 50m, 0m, 0m, 1, 0m));
        _dbContext.TicketOrders.Add(MakeOrder("ord_expensive", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 2, 10, 0), 500m, 0m, 0m, 1, 0m));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrdersPageAsync(
            null, "amount", false, 1, 25, null, null, null);

        result.Rows.First().VendorOrderId.Should().Be("ord_cheap");
        result.Rows.Last().VendorOrderId.Should().Be("ord_expensive");
    }

    // ====================================================================
    // GetAttendeesPageAsync tests
    // ====================================================================

    [Fact]
    public async Task GetAttendeesPageAsync_ReturnsPagedResults()
    {
        var order = MakeOrder("ord_1", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 300m, 0m, 0m, 3, 0m);
        _dbContext.TicketOrders.Add(order);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAttendeesPageAsync(
            null, "name", false, 1, 2, null, null, null, null);

        result.TotalCount.Should().Be(3);
        result.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAttendeesPageAsync_FiltersByTicketType()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_types",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 300m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            Attendees =
            [
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_fw",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "A1", TicketTypeName = "Full Week",
                    Price = 100m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                },
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_vip",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "A2", TicketTypeName = "VIP",
                    Price = 400m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                }
            ]
        };
        _dbContext.TicketOrders.Add(order);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAttendeesPageAsync(
            null, "name", false, 1, 25, "VIP", null, null, null);

        result.TotalCount.Should().Be(1);
        result.Rows.Single().TicketTypeName.Should().Be("VIP");
    }

    [Fact]
    public async Task GetAttendeesPageAsync_FiltersByOrderId()
    {
        _dbContext.TicketOrders.Add(MakeOrder("ord_A", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 100m, 0m, 0m, 1, 0m));
        _dbContext.TicketOrders.Add(MakeOrder("ord_B", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 2, 10, 0), 200m, 0m, 0m, 2, 0m));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAttendeesPageAsync(
            null, "name", false, 1, 25, null, null, null, "ord_A");

        result.TotalCount.Should().Be(1);
    }

    // ====================================================================
    // GetWhoHasntBoughtAsync tests
    // ====================================================================

    [Fact]
    public async Task GetWhoHasntBoughtAsync_ReturnsActiveHumansWithTicketStatus()
    {
        var team = new Team
        {
            Id = SystemTeamIds.Volunteers,
            Name = "Volunteers",
            Description = "All active volunteers"
        };
        _dbContext.Set<Team>().Add(team);

        var userWithTicket = CreateUser("Has Ticket", "hasticket@example.com");
        var userWithout = CreateUser("No Ticket", "noticket@example.com");

        await _dbContext.Users.AddRangeAsync(userWithTicket, userWithout);

        await _dbContext.Set<TeamMember>().AddRangeAsync(
            new TeamMember { Id = Guid.NewGuid(), TeamId = SystemTeamIds.Volunteers, UserId = userWithTicket.Id, Team = team },
            new TeamMember { Id = Guid.NewGuid(), TeamId = SystemTeamIds.Volunteers, UserId = userWithout.Id, Team = team }
        );

        // Create a ticket order matched to userWithTicket
        var orderId = Guid.NewGuid();
        _dbContext.TicketOrders.Add(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_1",
            BuyerName = "Has Ticket",
            BuyerEmail = "hasticket@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            MatchedUserId = userWithTicket.Id
        });

        await _dbContext.SaveChangesAsync();

        var result = await _service.GetWhoHasntBoughtAsync(null, null, null, null, 1, 25);

        result.TotalCount.Should().Be(2);
        result.Humans.Should().Contain(h => h.UserId == userWithTicket.Id && h.HasTicket);
        result.Humans.Should().Contain(h => h.UserId == userWithout.Id && !h.HasTicket);
    }

    [Fact]
    public async Task GetWhoHasntBoughtAsync_FiltersByTicketStatus()
    {
        var team = new Team
        {
            Id = SystemTeamIds.Volunteers,
            Name = "Volunteers",
            Description = "All active volunteers"
        };
        _dbContext.Set<Team>().Add(team);

        var userWithTicket = CreateUser("Has Ticket", "has@example.com");
        var userWithout = CreateUser("No Ticket", "no@example.com");
        await _dbContext.Users.AddRangeAsync(userWithTicket, userWithout);
        await _dbContext.Set<TeamMember>().AddRangeAsync(
            new TeamMember { Id = Guid.NewGuid(), TeamId = SystemTeamIds.Volunteers, UserId = userWithTicket.Id, Team = team },
            new TeamMember { Id = Guid.NewGuid(), TeamId = SystemTeamIds.Volunteers, UserId = userWithout.Id, Team = team }
        );

        _dbContext.TicketOrders.Add(new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = "ord_1",
            BuyerName = "Has Ticket",
            BuyerEmail = "has@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            MatchedUserId = userWithTicket.Id
        });

        await _dbContext.SaveChangesAsync();

        var notBought = await _service.GetWhoHasntBoughtAsync(null, null, null, "not_bought", 1, 25);
        notBought.TotalCount.Should().Be(1);
        notBought.Humans.Single().UserId.Should().Be(userWithout.Id);

        var bought = await _service.GetWhoHasntBoughtAsync(null, null, null, "bought", 1, 25);
        bought.TotalCount.Should().Be(1);
        bought.Humans.Single().UserId.Should().Be(userWithTicket.Id);
    }

    // ====================================================================
    // Export tests
    // ====================================================================

    [Fact]
    public async Task GetAttendeeExportDataAsync_ReturnsAllAttendeesOrderedByName()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_export",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 200m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            Attendees =
            [
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_z",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "Zara", TicketTypeName = "Full Week",
                    Price = 100m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                },
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_a",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "Alice", TicketTypeName = "VIP",
                    Price = 400m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                }
            ]
        };

        _dbContext.TicketOrders.Add(order);
        await _dbContext.SaveChangesAsync();

        var rows = await _service.GetAttendeeExportDataAsync();

        rows.Should().HaveCount(2);
        rows[0].AttendeeName.Should().Be("Alice");
        rows[1].AttendeeName.Should().Be("Zara");
        rows[0].VendorOrderId.Should().Be("ord_export");
    }

    [Fact]
    public async Task GetOrderExportDataAsync_ReturnsAllOrdersWithDetails()
    {
        _dbContext.TicketOrders.Add(MakeOrder("ord_old", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 1, 1, 10, 0), 100m, 5m, 9.09m, 1, 0m));
        _dbContext.TicketOrders.Add(MakeOrder("ord_new", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 200m, 10m, 18.18m, 2, 0m));
        await _dbContext.SaveChangesAsync();

        var rows = await _service.GetOrderExportDataAsync();

        rows.Should().HaveCount(2);
        // Ordered by purchase date descending
        rows[0].Date.Should().Be("2026-03-01");
        rows[1].Date.Should().Be("2026-01-01");
        rows[0].AttendeeCount.Should().Be(2);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static User CreateUser(string name, string email)
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = name,
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            Profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                MembershipTier = MembershipTier.Volunteer
            }
        };
        user.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsOAuth = true,
            IsVerified = true,
            IsNotificationTarget = true
        });
        return user;
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
