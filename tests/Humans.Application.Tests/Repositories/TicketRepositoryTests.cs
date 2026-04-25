using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Tickets;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

public sealed class TicketRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly TicketRepository _repo;

    public TicketRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new TicketRepository(new TestDbContextFactory(options));

        _dbContext.TicketSyncStates.Add(new TicketSyncState
        {
            Id = 1,
            SyncStatus = TicketSyncStatus.Idle,
            VendorEventId = string.Empty,
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── GetSyncStateAsync / PersistSyncStateAsync ────────────────────────────

    [HumansFact]
    public async Task GetSyncStateAsync_ReturnsSingletonRow()
    {
        var state = await _repo.GetSyncStateAsync();

        state.Should().NotBeNull();
        state!.Id.Should().Be(1);
        state.SyncStatus.Should().Be(TicketSyncStatus.Idle);
    }

    [HumansFact]
    public async Task PersistSyncStateAsync_UpdatesExistingSingleton()
    {
        var state = (await _repo.GetSyncStateAsync())!;
        state.SyncStatus = TicketSyncStatus.Running;
        state.LastError = "boom";
        state.StatusChangedAt = _clock.GetCurrentInstant();

        await _repo.PersistSyncStateAsync(state);

        var reloaded = await _dbContext.TicketSyncStates.AsNoTracking().FirstAsync(s => s.Id == 1);
        reloaded.SyncStatus.Should().Be(TicketSyncStatus.Running);
        reloaded.LastError.Should().Be("boom");
    }

    [HumansFact]
    public async Task ResetSyncStateLastSyncAsync_ClearsLastSyncAt()
    {
        var state = (await _repo.GetSyncStateAsync())!;
        state.LastSyncAt = _clock.GetCurrentInstant();
        await _repo.PersistSyncStateAsync(state);

        await _repo.ResetSyncStateLastSyncAsync();

        var reloaded = await _dbContext.TicketSyncStates.AsNoTracking().FirstAsync(s => s.Id == 1);
        reloaded.LastSyncAt.Should().BeNull();
    }

    // ── UpsertOrdersAsync ────────────────────────────────────────────────────

    [HumansFact]
    public async Task UpsertOrdersAsync_InsertsNewRowsAndUpdatesExisting()
    {
        var existing = new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = "ord_existing",
            BuyerName = "Old Name",
            BuyerEmail = "old@example.com",
            TotalAmount = 10m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_1",
            PurchasedAt = _clock.GetCurrentInstant(),
            SyncedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.TicketOrders.Add(existing);
        await _dbContext.SaveChangesAsync();

        var updatedExisting = new TicketOrder
        {
            Id = existing.Id,
            VendorOrderId = "ord_existing",
            BuyerName = "New Name",
            BuyerEmail = "new@example.com",
            TotalAmount = 99m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_1",
            PurchasedAt = _clock.GetCurrentInstant(),
            SyncedAt = _clock.GetCurrentInstant(),
        };
        var brandNew = new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = "ord_new",
            BuyerName = "Brand New",
            BuyerEmail = "new@example.com",
            TotalAmount = 50m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Pending,
            VendorEventId = "ev_1",
            PurchasedAt = _clock.GetCurrentInstant(),
            SyncedAt = _clock.GetCurrentInstant(),
        };

        await _repo.UpsertOrdersAsync(new List<TicketOrder> { updatedExisting, brandNew });

        var rows = await _dbContext.TicketOrders.AsNoTracking()
            .OrderBy(o => o.VendorOrderId).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].VendorOrderId.Should().Be("ord_existing");
        rows[0].BuyerName.Should().Be("New Name");
        rows[0].TotalAmount.Should().Be(99m);
        rows[1].VendorOrderId.Should().Be("ord_new");
    }

    // ── GetAllUserEmailLookupEntriesAsync ────────────────────────────────────

    [HumansFact]
    public async Task GetAllUserEmailLookupEntriesAsync_ReturnsOneEntryPerRow()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "u@x.com",
            Email = "u@x.com",
            DisplayName = "U",
        };
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "primary@example.com",
            IsOAuth = true,
            IsVerified = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "alt@example.com",
            IsOAuth = false,
            IsVerified = false,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        var entries = await _repo.GetAllUserEmailLookupEntriesAsync();

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Email == "primary@example.com" && e.IsOAuth);
        entries.Should().Contain(e => e.Email == "alt@example.com" && !e.IsOAuth);
    }

    // ── GetMatchedAttendeesForEventAsync ─────────────────────────────────────

    [HumansFact]
    public async Task GetMatchedAttendeesForEventAsync_FiltersToEventAndMatchedOnly()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_1",
            VendorEventId = "ev_a",
            BuyerEmail = "b@e.com",
            BuyerName = "B",
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            PurchasedAt = _clock.GetCurrentInstant(),
            SyncedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.TicketOrders.Add(order);

        var matchedUserId = Guid.NewGuid();

        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_matched_a",
            TicketOrderId = orderId,
            AttendeeName = "Matched A",
            VendorEventId = "ev_a",
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = matchedUserId,
            SyncedAt = _clock.GetCurrentInstant(),
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_matched_b",
            TicketOrderId = orderId,
            AttendeeName = "Matched B (other event)",
            VendorEventId = "ev_b",
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = Guid.NewGuid(),
            SyncedAt = _clock.GetCurrentInstant(),
        });
        _dbContext.TicketAttendees.Add(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tk_unmatched",
            TicketOrderId = orderId,
            AttendeeName = "Unmatched",
            VendorEventId = "ev_a",
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = null,
            SyncedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        var rows = await _repo.GetMatchedAttendeesForEventAsync("ev_a");

        rows.Should().ContainSingle();
        rows[0].MatchedUserId.Should().Be(matchedUserId);
        rows[0].Status.Should().Be(TicketAttendeeStatus.Valid);
    }
}
