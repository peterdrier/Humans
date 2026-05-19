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

namespace Humans.Application.Tests.Services;

/// <summary>
/// Tests for the null-VendorOrderId branch in
/// <see cref="TicketSyncService.SyncOrdersAndAttendeesAsync"/>.
/// API-issued tickets (e.g. reissued transfers) arrive with no order_id from
/// the vendor; the service must fall back to the existing local row's parent,
/// or skip with a warning when no local row exists.
/// </summary>
public sealed class TicketSyncServiceNullOrderTests : ServiceTestHarness
{
    private readonly ITicketVendorService _vendorService;
    private readonly IStripeService _stripeService;
    private readonly ICampaignService _campaignService;
    private readonly IUserService _userService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly ITicketRepository _ticketRepository;
    private readonly TicketSyncService _service;

    public TicketSyncServiceNullOrderTests()
    {
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
            _campaignService,
            _shiftManagementService);

        // Seed the singleton TicketSyncState row (required by the service)
        Db.TicketSyncStates.Add(new TicketSyncState
        {
            Id = 1,
            SyncStatus = TicketSyncStatus.Idle,
            VendorEventId = string.Empty
        });
        Db.SaveChanges();
    }

    // ==========================================================================
    // Test 1: null VendorOrderId + local row exists → upsert succeeds
    // ==========================================================================

    /// <summary>
    /// An API-issued ticket with no order_id already has a local row from a
    /// previous sync. The service should resolve the parent via the existing
    /// row's TicketOrderId and upsert the attendee (not skip it).
    /// </summary>
    [HumansFact]
    public async Task SyncOrdersAndAttendeesAsync_NullVendorOrderId_ExistingLocalRow_UpsertsSucessfully()
    {
        // Arrange — seed a parent order and a pre-existing attendee row.
        var parentOrderId = Guid.NewGuid();
        var parentOrder = new TicketOrder
        {
            Id = parentOrderId,
            VendorOrderId = "ord_parent",
            BuyerName = "Alice Buyer",
            BuyerEmail = "alice@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test_123",
            PurchasedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            SyncedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        };
        Db.TicketOrders.Add(parentOrder);

        var existingAttendeeId = Guid.NewGuid();
        var existingAttendee = new TicketAttendee
        {
            Id = existingAttendeeId,
            VendorTicketId = "tkt_api_issued",
            TicketOrderId = parentOrderId,
            AttendeeName = "Bob Recipient",
            AttendeeEmail = "bob@example.com",
            TicketTypeName = "Full Week",
            Price = 100m,
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = "ev_test_123",
            SyncedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        };
        Db.TicketAttendees.Add(existingAttendee);
        await Db.SaveChangesAsync();

        // Vendor returns the same ticket, but now with null VendorOrderId
        // (as happens when TT returns API-issued tickets with no order association).
        var ticket = new VendorTicketDto(
            VendorTicketId: "tkt_api_issued",
            VendorOrderId: null,
            AttendeeName: "Bob Recipient Updated",
            AttendeeEmail: "bob@example.com",
            TicketTypeName: "Full Week",
            Price: 100m,
            Status: "valid");

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorOrderDto>());
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto> { ticket });

        // Act
        var result = await _service.SyncOrdersAndAttendeesAsync();

        // Assert — the attendee row was upserted (not skipped)
        result.AttendeesSynced.Should().Be(1);

        var dbAttendees = await Db.TicketAttendees.AsNoTracking().ToListAsync();
        dbAttendees.Should().ContainSingle();
        dbAttendees[0].VendorTicketId.Should().Be("tkt_api_issued");
        dbAttendees[0].AttendeeName.Should().Be("Bob Recipient Updated");
        dbAttendees[0].TicketOrderId.Should().Be(parentOrderId);
    }

    // ==========================================================================
    // Test 2: null VendorOrderId + no local row → skip with warning
    // ==========================================================================

    /// <summary>
    /// An API-issued ticket with no order_id and no pre-existing local row is
    /// an orphan that cannot be linked to any order. The service should skip it
    /// and log a warning. AttendeesSynced must not include the skipped ticket.
    /// </summary>
    [HumansFact]
    public async Task SyncOrdersAndAttendeesAsync_NullVendorOrderId_NoLocalRow_SkipsWithoutUpsert()
    {
        // No pre-existing data for this ticket ID.
        var ticket = new VendorTicketDto(
            VendorTicketId: "tkt_orphan",
            VendorOrderId: null,
            AttendeeName: "Carol Orphan",
            AttendeeEmail: "carol@example.com",
            TicketTypeName: "Full Week",
            Price: 100m,
            Status: "valid");

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorOrderDto>());
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VendorTicketDto> { ticket });

        // Act
        var result = await _service.SyncOrdersAndAttendeesAsync();

        // Assert — skipped; no attendee rows written
        result.AttendeesSynced.Should().Be(0);

        var dbAttendees = await Db.TicketAttendees.AsNoTracking().ToListAsync();
        dbAttendees.Should().BeEmpty();
    }

    // ==========================================================================
    // Test 3: known VendorOrderId → existing happy path still works
    // ==========================================================================

    /// <summary>
    /// A ticket with a known VendorOrderId (the normal case) continues to sync
    /// correctly — the null-order branch is not entered.
    /// </summary>
    [HumansFact]
    public async Task SyncOrdersAndAttendeesAsync_KnownVendorOrderId_SyncsNormally()
    {
        var orders = new List<VendorOrderDto>
        {
            new(
                VendorOrderId: "ord_normal",
                BuyerName: "Dave Buyer",
                BuyerEmail: "dave@example.com",
                TotalAmount: 100m,
                Currency: "EUR",
                DiscountCode: null,
                PaymentStatus: "completed",
                VendorDashboardUrl: null,
                PurchasedAt: Instant.FromUtc(2026, 2, 1, 0, 0),
                Tickets: [])
        };

        var tickets = new List<VendorTicketDto>
        {
            new(
                VendorTicketId: "tkt_normal",
                VendorOrderId: "ord_normal",
                AttendeeName: "Dave Buyer",
                AttendeeEmail: "dave@example.com",
                TicketTypeName: "Full Week",
                Price: 100m,
                Status: "valid")
        };

        _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(tickets);

        // Act
        var result = await _service.SyncOrdersAndAttendeesAsync();

        // Assert
        result.OrdersSynced.Should().Be(1);
        result.AttendeesSynced.Should().Be(1);

        var dbAttendees = await Db.TicketAttendees.AsNoTracking().ToListAsync();
        dbAttendees.Should().ContainSingle();
        dbAttendees[0].VendorTicketId.Should().Be("tkt_normal");

        var dbOrders = await Db.TicketOrders.AsNoTracking().ToListAsync();
        dbOrders.Should().ContainSingle();
        dbOrders[0].VendorOrderId.Should().Be("ord_normal");
    }
}
