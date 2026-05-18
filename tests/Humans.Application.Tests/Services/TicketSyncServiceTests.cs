using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Application.Tests.Services;

public class TicketSyncServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly FakeClock _clock;
    private readonly ITicketVendorService _vendorService;
    private readonly IStripeService _stripeService;
    private readonly ICampaignService _campaignService;
    private readonly IUserService _userService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly ITicketRepository _ticketRepository;
    private readonly TicketSyncService _service;

    public TicketSyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _factory = new TestDbContextFactory(options);
        _dbContext = _factory.CreateDbContext();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _vendorService = Substitute.For<ITicketVendorService>();

        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test_123",
            SyncIntervalMinutes = 15,
            ApiKey = "test_key"
        });

        _stripeService = Substitute.For<IStripeService>();
        _userService = Substitute.For<IUserService>();
        _userService.GetAllParticipationsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _campaignService = Substitute.For<ICampaignService>();
        _shiftManagementService = Substitute.For<IShiftManagementService>();

        _ticketRepository = new TicketRepository(_factory);

        _service = new TicketSyncService(
            _ticketRepository,
            new TicketTransferRepository(_factory),
            _vendorService,
            _stripeService,
            _clock,
            settings,
            NullLogger<TicketSyncService>.Instance,
            Substitute.For<ITicketCacheInvalidator>(),
            _userService,
            _campaignService,
            _shiftManagementService);

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

    [HumansFact]
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
        dbOrders.Select(o => o.VendorOrderId).Should().BeEquivalentTo("ord_001", "ord_002");
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_MatchesOrderToUserByEmail
    // ==========================================================================

    [HumansFact]
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

    [HumansFact]
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

    [HumansFact]
    public async Task SyncOrdersAndAttendeesAsync_ForwardsDiscountCodesToCampaignService()
    {
        // Order uses a discount code — TicketSyncService is expected to aggregate
        // these into DiscountCodeRedemption instances and delegate to ICampaignService
        // (which owns CampaignGrants) for the actual RedeemedAt updates.
        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_disc", "Buyer", "buyer@example.com", discountCode: "discount10")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>());

        _campaignService.MarkGrantsRedeemedAsync(
            Arg.Any<IReadOnlyCollection<DiscountCodeRedemption>>(),
            Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await _service.SyncOrdersAndAttendeesAsync();

        result.CodesRedeemed.Should().Be(1);

        // Verify the redemption was forwarded to the campaign service
        await _campaignService.Received(1).MarkGrantsRedeemedAsync(
            Arg.Is<IReadOnlyCollection<DiscountCodeRedemption>>(r =>
                r.Count == 1 && string.Equals(r.First().Code, "discount10", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_SetsErrorStateOnFailure
    // ==========================================================================

    [HumansFact]
    public async Task SyncOrdersAndAttendeesAsync_TransientError_ReturnsGracefully()
    {
        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("API unavailable"));

        var result = await _service.SyncOrdersAndAttendeesAsync();

        result.OrdersSynced.Should().Be(0);
        var syncState = await _dbContext.TicketSyncStates.AsNoTracking()
            .FirstAsync(s => s.Id == 1);
        syncState.SyncStatus.Should().Be(TicketSyncStatus.Idle);
        syncState.LastError.Should().BeNull();
    }

    [HumansFact]
    public async Task SyncOrdersAndAttendeesAsync_NonTransientError_SetsErrorState()
    {
        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized));

        var act = () => _service.SyncOrdersAndAttendeesAsync();

        await act.Should().ThrowAsync<HttpRequestException>();

        var syncState = await _dbContext.TicketSyncStates.AsNoTracking()
            .FirstAsync(s => s.Id == 1);
        syncState.SyncStatus.Should().Be(TicketSyncStatus.Error);
        syncState.LastError.Should().Be("Unauthorized");
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_SkipsWhenEventIdNotConfigured
    // ==========================================================================

    [HumansFact]
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
            _ticketRepository,
            new TicketTransferRepository(_factory),
            _vendorService,
            _stripeService,
            _clock,
            settings,
            NullLogger<TicketSyncService>.Instance,
            Substitute.For<ITicketCacheInvalidator>(),
            Substitute.For<IUserService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IShiftManagementService>());

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

    [HumansFact]
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

    [HumansFact]
    public async Task SyncOrdersAndAttendeesAsync_ComputesVatUsingOnlyNonVoidTickets()
    {
        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_vat", "Buyer", "buyer@example.com", totalAmount: 900m)
        };

        var tickets = new List<VendorTicketDto>
        {
            MakeTicketDto("tkt_valid_vip", "ord_vat", "Valid VIP", "valid@example.com", 400m, "valid"),
            MakeTicketDto("tkt_void_vip", "ord_vat", "Void VIP", "void@example.com", 500m, "void")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(tickets);

        await _service.SyncOrdersAndAttendeesAsync();

        var order = await _dbContext.TicketOrders.SingleAsync();
        order.VatAmount.Should().Be(28.64m);
    }

    [HumansFact(Timeout = 10000)]
    public async Task SyncOrdersAndAttendeesAsync_StoresZeroVatForRefundedOrCancelledOrders()
    {
        var orders = new List<VendorOrderDto>
        {
            MakeOrderDto("ord_refunded", "Refunded Buyer", "refunded@example.com", totalAmount: 400m, paymentStatus: "refunded"),
            MakeOrderDto("ord_cancelled", "Cancelled Buyer", "cancelled@example.com", totalAmount: 400m, paymentStatus: "cancelled")
        };

        var tickets = new List<VendorTicketDto>
        {
            MakeTicketDto("tkt_refunded", "ord_refunded", "Refunded VIP", "refunded@example.com", 400m, "valid"),
            MakeTicketDto("tkt_cancelled", "ord_cancelled", "Cancelled VIP", "cancelled@example.com", 400m, "valid")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(tickets);

        await _service.SyncOrdersAndAttendeesAsync();

        var syncedOrders = await _dbContext.TicketOrders
            .OrderBy(o => o.VendorOrderId)
            .ToListAsync();

        syncedOrders.Should().HaveCount(2);
        syncedOrders.Should().OnlyContain(o => o.VatAmount == 0m);
    }

    // ==========================================================================
    // SyncOrdersAndAttendeesAsync_HandlesRealisticScale
    // ==========================================================================

    [HumansFact]
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
        var syncState = await _dbContext.TicketSyncStates.AsNoTracking()
            .FirstAsync(s => s.Id == 1);
        syncState.SyncStatus.Should().Be(TicketSyncStatus.Idle);
        syncState.LastSyncAt.Should().NotBeNull();
    }

    // ==========================================================================
    // EventParticipation.CheckedInAt threading (#736)
    // ==========================================================================

    [HumansFact]
    public async Task SyncEventParticipations_PassesVendorCheckedInAt_ForCheckedInAttendees()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        SeedUserEmail(userId, "alice@example.com", isOAuth: true);
        await _dbContext.SaveChangesAsync();

        _shiftManagementService.GetActiveAsync()
            .Returns(new EventSettings { Year = 2026 });

        var checkInInstant = Instant.FromUtc(2026, 7, 8, 14, 30);

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorOrderDto> { MakeOrderDto("ord_chk", "Alice", "alice@example.com") });
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>
            {
                MakeTicketDto("tkt_chk", "ord_chk", "Alice", "alice@example.com",
                    status: "checked_in", checkedInAt: checkInInstant)
            });

        await _service.SyncOrdersAndAttendeesAsync();

        await _userService.Received(1).SetParticipationFromTicketSyncAsync(
            userId, 2026, ParticipationStatus.Attended,
            checkInInstant, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncEventParticipations_TakesMinCheckedInAt_AcrossUsersCheckedInAttendees()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        SeedUserEmail(userId, "alice@example.com", isOAuth: true);
        await _dbContext.SaveChangesAsync();

        _shiftManagementService.GetActiveAsync()
            .Returns(new EventSettings { Year = 2026 });

        var earlier = Instant.FromUtc(2026, 7, 8, 10, 0);
        var later = Instant.FromUtc(2026, 7, 8, 18, 0);

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorOrderDto> { MakeOrderDto("ord_two", "Alice", "alice@example.com") });
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>
            {
                MakeTicketDto("tkt_a", "ord_two", "Alice", "alice@example.com",
                    status: "checked_in", checkedInAt: later),
                MakeTicketDto("tkt_b", "ord_two", "Alice", "alice@example.com",
                    status: "checked_in", checkedInAt: earlier),
            });

        await _service.SyncOrdersAndAttendeesAsync();

        await _userService.Received(1).SetParticipationFromTicketSyncAsync(
            userId, 2026, ParticipationStatus.Attended,
            earlier, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncEventParticipations_PassesNullCheckedInAt_WhenVendorDidntReturnTimestamp()
    {
        // Graceful fallback: vendor returned status=checked_in but no
        // check_in.checked_in_at — sync still flips to Attended but with null
        // timestamp. Repo's "never overwrite non-null" rule preserves a prior
        // timestamp if one already exists.
        var userId = Guid.NewGuid();
        SeedUser(userId);
        SeedUserEmail(userId, "alice@example.com", isOAuth: true);
        await _dbContext.SaveChangesAsync();

        _shiftManagementService.GetActiveAsync()
            .Returns(new EventSettings { Year = 2026 });

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorOrderDto> { MakeOrderDto("ord_x", "Alice", "alice@example.com") });
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>
            {
                MakeTicketDto("tkt_x", "ord_x", "Alice", "alice@example.com",
                    status: "checked_in", checkedInAt: null)
            });

        await _service.SyncOrdersAndAttendeesAsync();

        await _userService.Received(1).SetParticipationFromTicketSyncAsync(
            userId, 2026, ParticipationStatus.Attended,
            (Instant?)null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncEventParticipations_PassesNullCheckedInAt_ForTicketedOnlyAttendees()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        SeedUserEmail(userId, "alice@example.com", isOAuth: true);
        await _dbContext.SaveChangesAsync();

        _shiftManagementService.GetActiveAsync()
            .Returns(new EventSettings { Year = 2026 });

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorOrderDto> { MakeOrderDto("ord_v", "Alice", "alice@example.com") });
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>
            {
                MakeTicketDto("tkt_v", "ord_v", "Alice", "alice@example.com",
                    status: "valid", checkedInAt: null)
            });

        await _service.SyncOrdersAndAttendeesAsync();

        await _userService.Received(1).SetParticipationFromTicketSyncAsync(
            userId, 2026, ParticipationStatus.Ticketed,
            (Instant?)null, Arg.Any<CancellationToken>());
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
            Provider = isOAuth ? "Google" : null,
            ProviderKey = isOAuth ? $"test-{Guid.NewGuid()}" : null,
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
        string? discountCode = null,
        string paymentStatus = "completed")
    {
        return new VendorOrderDto(
            VendorOrderId: vendorOrderId,
            BuyerName: buyerName,
            BuyerEmail: buyerEmail,
            TotalAmount: totalAmount,
            Currency: "EUR",
            DiscountCode: discountCode,
            PaymentStatus: paymentStatus,
            VendorDashboardUrl: null,
            PurchasedAt: Instant.FromUtc(2026, 2, 15, 10, 0),
            Tickets: []);
    }

    private static VendorTicketDto MakeTicketDto(
        string vendorTicketId,
        string vendorOrderId,
        string attendeeName,
        string? attendeeEmail,
        decimal price = 50m,
        string status = "valid",
        Instant? checkedInAt = null)
    {
        return new VendorTicketDto(
            VendorTicketId: vendorTicketId,
            VendorOrderId: vendorOrderId,
            AttendeeName: attendeeName,
            AttendeeEmail: attendeeEmail,
            TicketTypeName: "Full Week",
            Price: price,
            Status: status,
            CheckedInAt: checkedInAt);
    }
}
