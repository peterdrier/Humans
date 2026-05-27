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
using Humans.Infrastructure.Repositories.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Application.Tests.Services;

public sealed class TicketSyncServiceTests : ServiceTestHarness
{
    private readonly ITicketVendorService _vendorService;
    private readonly IStripeService _stripeService;
    private readonly ICampaignService _campaignService;
    private readonly IUserService _userService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly ITicketRepository _ticketRepository;
    private readonly TicketSyncService _service;

    public TicketSyncServiceTests()
    {
        _vendorService = Substitute.For<ITicketVendorService>();

        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test_123",
            SyncIntervalMinutes = 15,
            ApiKey = "test_key"
        });

        _stripeService = Substitute.For<IStripeService>();
        _userService = NewDbBackedUserService();
        _userService.GetAllParticipationsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _campaignService = Substitute.For<ICampaignService>();
        _shiftManagementService = Substitute.For<IShiftManagementService>();

        _ticketRepository = new TicketRepository(DbFactory);

        _service = new TicketSyncService(
            _ticketRepository,
            new TicketTransferRepository(DbFactory),
            _vendorService,
            _stripeService,
            Clock,
            settings,
            NullLogger<TicketSyncService>.Instance,
            Substitute.For<ITicketCacheInvalidator>(),
            _userService,
            _userService,
            _campaignService,
            _shiftManagementService);

        // Seed the singleton TicketSyncState row
        Db.TicketSyncStates.Add(new TicketSyncState
        {
            Id = 1,
            SyncStatus = TicketSyncStatus.Idle,
            VendorEventId = string.Empty
        });
        Db.SaveChanges();
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

        var dbOrders = await Db.TicketOrders.ToListAsync();
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
        await Db.SaveChangesAsync();

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

        var dbOrder = await Db.TicketOrders.SingleAsync();
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

        var dbOrders = await Db.TicketOrders.ToListAsync();
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
        var syncState = await Db.TicketSyncStates.AsNoTracking()
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

        var syncState = await Db.TicketSyncStates.AsNoTracking()
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
            new TicketTransferRepository(DbFactory),
            _vendorService,
            _stripeService,
            Clock,
            settings,
            NullLogger<TicketSyncService>.Instance,
            Substitute.For<ITicketCacheInvalidator>(),
            _userService,
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

        var dbOrders = await Db.TicketOrders.ToListAsync();
        dbOrders.Should().ContainSingle();

        var dbAttendees = await Db.TicketAttendees.ToListAsync();
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

        var order = await Db.TicketOrders.SingleAsync();
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

        var syncedOrders = await Db.TicketOrders
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

        var dbOrders = await Db.TicketOrders.CountAsync();
        dbOrders.Should().Be(500);

        var dbAttendees = await Db.TicketAttendees.CountAsync();
        dbAttendees.Should().Be(700);

        // Sync state should be Idle after success
        var syncState = await Db.TicketSyncStates.AsNoTracking()
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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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

    [HumansFact]
    public async Task SyncEventParticipations_SkipsWrite_WhenCacheAlreadyMatches()
    {
        // User already recorded as Ticketed-from-sync for the active year, and the
        // vendor still shows a valid ticket. Nothing changed, so the per-user upsert
        // (and the cache-slice refresh it triggers) must be skipped entirely.
        var userId = Guid.NewGuid();
        SeedUser(userId);
        SeedUserEmail(userId, "alice@example.com", isOAuth: true);
        await Db.SaveChangesAsync();

        _shiftManagementService.GetActiveAsync()
            .Returns(new EventSettings { Year = 2026 });

        _userService.GetAllParticipationsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<EventParticipation>
            {
                new()
                {
                    UserId = userId,
                    Year = 2026,
                    Status = ParticipationStatus.Ticketed,
                    Source = ParticipationSource.TicketSync
                }
            });

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorOrderDto> { MakeOrderDto("ord_same", "Alice", "alice@example.com") });
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto>
            {
                MakeTicketDto("tkt_same", "ord_same", "Alice", "alice@example.com", status: "valid")
            });

        await _service.SyncOrdersAndAttendeesAsync();

        await _userService.DidNotReceive().SetParticipationFromTicketSyncAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<ParticipationStatus>(),
            Arg.Any<Instant?>(), Arg.Any<CancellationToken>());
        await _userService.DidNotReceive().RemoveTicketSyncParticipationAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncEventParticipations_StillWrites_WhenTicketedHolderChecksIn()
    {
        // Cache shows Ticketed; vendor now reports checked-in. The status genuinely
        // changes (Ticketed → Attended), so the upsert must still fire.
        var userId = Guid.NewGuid();
        SeedUser(userId);
        SeedUserEmail(userId, "alice@example.com", isOAuth: true);
        await Db.SaveChangesAsync();

        _shiftManagementService.GetActiveAsync()
            .Returns(new EventSettings { Year = 2026 });

        _userService.GetAllParticipationsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<EventParticipation>
            {
                new()
                {
                    UserId = userId,
                    Year = 2026,
                    Status = ParticipationStatus.Ticketed,
                    Source = ParticipationSource.TicketSync
                }
            });

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

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private UserEmail SeedUserEmail(
        Guid userId, string email,
        bool isOAuth = false, bool isVerified = true)
    {
        var now = Clock.GetCurrentInstant();
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
        Db.UserEmails.Add(userEmail);
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
