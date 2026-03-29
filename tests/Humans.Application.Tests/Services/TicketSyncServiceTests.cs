using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TicketSyncServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ITicketVendorService _vendorService;
    private readonly IStripeService _stripeService;
    private readonly TicketSyncService _service;

    public TicketSyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _vendorService = Substitute.For<ITicketVendorService>();

        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test_123",
            SyncIntervalMinutes = 15,
            ApiKey = "test_key"
        });

        _stripeService = Substitute.For<IStripeService>();
        _service = new TicketSyncService(
            _dbContext,
            _vendorService,
            _stripeService,
            _clock,
            settings,
            NullLogger<TicketSyncService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        // Seed the singleton TicketSyncState row
        _dbContext.TicketSyncStates.Add(new TicketSyncState
        {
            Id = 1,
            SyncStatus = TicketSyncStatus.Idle,
            VendorEventId = string.Empty
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_InsertsNewOrders
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_InsertsNewOrders()
    {
        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_001", "Alice Smith", "alice@example.com"),
            MakeOrderDto("ord_002", "Bob Jones", "bob@example.com")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>());

        var result = await _service.SyncOrdersAndAttendeesAsync();

        result.OrdersSynced.Should().Be(2);
        result.AttendeesSynced.Should().Be(0);

        var dbOrders = await _dbContext.TicketOrders.ToListAsync();
        dbOrders.Should().HaveCount(2);
        dbOrders.Select(o => o.VendorOrderId).Should().BeEquivalentTo(new[] { "ord_001", "ord_002" });
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_MatchesOrderToUserByEmail
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_MatchesOrderToUserByEmail()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        // Seed email in LOWERCASE — order will use UPPERCASE to test case-insensitivity
        SeedUserEmail(userId, "alice@example.com", isOAuth: true);
        await _dbContext.SaveChangesAsync();

        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_match", "Alice", "ALICE@EXAMPLE.COM")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>());

        var result = await _service.SyncOrdersAndAttendeesAsync();

        result.OrdersMatched.Should().Be(1);

        var dbOrder = await _dbContext.TicketOrders.SingleAsync();
        dbOrder.MatchedUserId.Should().Be(userId);
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_UpsertDoesNotCreateDuplicates
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_UpsertDoesNotCreateDuplicates()
    {
        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_dup", "Alice", "alice@example.com", totalAmount: 50m)
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>());

        // First sync
        await _service.SyncOrdersAndAttendeesAsync();

        // Update the order's total for second sync
        var updatedOrders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_dup", "Alice Updated", "alice@example.com", totalAmount: 75m)
        };
        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(updatedOrders);

        // Second sync
        await _service.SyncOrdersAndAttendeesAsync();

        var dbOrders = await _dbContext.TicketOrders.ToListAsync();
        dbOrders.Should().ContainSingle();
        dbOrders[0].BuyerName.Should().Be("Alice Updated");
        dbOrders[0].TotalAmount.Should().Be(75m);
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_MatchesDiscountCodeToCampaignGrant
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_MatchesDiscountCodeToCampaignGrant()
    {
        // Seed campaign infrastructure
        var creatorId = Guid.NewGuid();
        SeedUser(creatorId, "Creator");

        var campaignId = Guid.NewGuid();
        var codeId = Guid.NewGuid();
        var grantUserId = Guid.NewGuid();
        SeedUser(grantUserId, "GrantUser");

        var campaign = new Campaign
        {
            Id = campaignId,
            Title = "Test Campaign",
            EmailSubject = "Your code",
            EmailBodyTemplate = "<p>{{Code}}</p>",
            Status = CampaignStatus.Active,
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = creatorId
        };
        _dbContext.Campaigns.Add(campaign);

        var code = new CampaignCode
        {
            Id = codeId,
            CampaignId = campaignId,
            Code = "DISCOUNT10",
            ImportedAt = _clock.GetCurrentInstant()
        };
        _dbContext.CampaignCodes.Add(code);

        var grant = new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CampaignCodeId = codeId,
            UserId = grantUserId,
            AssignedAt = _clock.GetCurrentInstant(),
            RedeemedAt = null
        };
        _dbContext.CampaignGrants.Add(grant);
        await _dbContext.SaveChangesAsync();

        // Order uses the discount code
        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_disc", "Buyer", "buyer@example.com", discountCode: "discount10")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>());

        var result = await _service.SyncOrdersAndAttendeesAsync();

        result.CodesRedeemed.Should().Be(1);

        var updatedGrant = await _dbContext.CampaignGrants.FindAsync(grant.Id);
        updatedGrant!.RedeemedAt.Should().NotBeNull();
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_SetsErrorStateOnFailure
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_SetsErrorStateOnFailure()
    {
        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("API unavailable"));

        var act = () => _service.SyncOrdersAndAttendeesAsync();

        await act.Should().ThrowAsync<HttpRequestException>();

        var syncState = await _dbContext.TicketSyncStates.FindAsync(1);
        syncState!.SyncStatus.Should().Be(TicketSyncStatus.Error);
        syncState.LastError.Should().Be("API unavailable");
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_SkipsWhenEventIdNotConfigured
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_SkipsWhenEventIdNotConfigured()
    {
        // Create a service with empty EventId
        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "",
            SyncIntervalMinutes = 15,
            ApiKey = "test_key"
        });

        var service = new TicketSyncService(
            _dbContext,
            _vendorService,
            _stripeService,
            _clock,
            settings,
            NullLogger<TicketSyncService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        var result = await service.SyncOrdersAndAttendeesAsync();

        result.OrdersSynced.Should().Be(0);
        result.AttendeesSynced.Should().Be(0);
        result.CodesRedeemed.Should().Be(0);

        // Vendor service should NOT have been called
        await _vendorService.DidNotReceive()
            .GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_AttendeeUpsertDoesNotCreateDuplicates
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_AttendeeUpsertDoesNotCreateDuplicates()
    {
        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_att", "Alice", "alice@example.com")
        };
        var tickets = new List<VendorTicketDto>
        {
            MakeTicketDto("tkt_001", "ord_att", "Alice", "alice@example.com")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(tickets);

        // First sync
        await _service.SyncOrdersAndAttendeesAsync();

        // Second sync with same data
        await _service.SyncOrdersAndAttendeesAsync();

        var dbOrders = await _dbContext.TicketOrders.ToListAsync();
        dbOrders.Should().ContainSingle();

        var dbAttendees = await _dbContext.TicketAttendees.ToListAsync();
        dbAttendees.Should().ContainSingle();
        dbAttendees[0].AttendeeName.Should().Be("Alice");
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_HandlesRealisticScale
    // ==========================================================================

    [Fact]
    public async Task SyncOrdersAndAttendeesAsync_HandlesRealisticScale()
    {
        var orders = Enumerable.Range(1, 500)
            .Select(i => MakeOrderDto($"ord_{i:D4}", $"Buyer {i}", $"buyer{i}@example.com"))
            .ToList();

        // 700 attendees spread across the first 500 orders
        // (some orders get 2 attendees, some get 1)
        var tickets = Enumerable.Range(1, 700)
            .Select(i => MakeTicketDto(
                $"tkt_{i:D4}",
                $"ord_{((i - 1) % 500) + 1:D4}",
                $"Attendee {i}",
                $"attendee{i}@example.com"))
            .ToList();

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(tickets);

        var result = await _service.SyncOrdersAndAttendeesAsync();

        result.OrdersSynced.Should().Be(500);
        result.AttendeesSynced.Should().Be(700);

        var dbOrders = await _dbContext.TicketOrders.CountAsync();
        dbOrders.Should().Be(500);

        var dbAttendees = await _dbContext.TicketAttendees.CountAsync();
        dbAttendees.Should().Be(700);

        // Sync state should be Idle after success
        var syncState = await _dbContext.TicketSyncStates.FindAsync(1);
        syncState!.SyncStatus.Should().Be(TicketSyncStatus.Idle);
        syncState.LastSyncAt.Should().NotBeNull();
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private User SeedUser(Guid? id = null, string displayName = "Test User")
    {
        var userId = id ?? Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private UserEmail SeedUserEmail(
        Guid userId, string email,
        bool isOAuth = false, bool isVerified = true)
    {
        var now = _clock.GetCurrentInstant();
        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsOAuth = isOAuth,
            IsVerified = isVerified,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.UserEmails.Add(userEmail);
        return userEmail;
    }

    private static VendorOrderDto MakeOrderDto(
        string vendorOrderId,
        string buyerName,
        string buyerEmail,
        decimal totalAmount = 100m,
        string? discountCode = null)
    {
        return new VendorOrderDto(
            VendorOrderId: vendorOrderId,
            BuyerName: buyerName,
            BuyerEmail: buyerEmail,
            TotalAmount: totalAmount,
            Currency: "EUR",
            DiscountCode: discountCode,
            PaymentStatus: "completed",
            VendorDashboardUrl: null,
            PurchasedAt: Instant.FromUtc(2026, 2, 15, 10, 0),
            Tickets: []);
    }

    private static VendorTicketDto MakeTicketDto(
        string vendorTicketId,
        string vendorOrderId,
        string attendeeName,
        string? attendeeEmail)
    {
        return new VendorTicketDto(
            VendorTicketId: vendorTicketId,
            VendorOrderId: vendorOrderId,
            AttendeeName: attendeeName,
            AttendeeEmail: attendeeEmail,
            TicketTypeName: "Full Week",
            Price: 50m,
            Status: "valid");
    }
}
