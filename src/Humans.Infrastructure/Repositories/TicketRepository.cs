using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="ITicketRepository"/>. The only
/// non-test file that writes to <c>ticket_orders</c>, <c>ticket_attendees</c>,
/// and <c>ticket_sync_states</c> after the TicketSyncService migration lands
/// (per PR #545c / umbrella #545).
/// </summary>
/// <remarks>
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains short-lived
/// per method — same pattern as <c>ProfileRepository</c>, <c>UserRepository</c>,
/// and <c>TicketingBudgetRepository</c> (design-rules §15b).
/// </remarks>
public sealed class TicketRepository : ITicketRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public TicketRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ── TicketSyncState ──────────────────────────────────────────────────────

    public async Task<TicketSyncState?> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketSyncStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
    }

    public async Task PersistSyncStateAsync(TicketSyncState state, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.TicketSyncStates.FirstOrDefaultAsync(s => s.Id == state.Id, ct);
        if (existing is null)
        {
            ctx.TicketSyncStates.Add(state);
        }
        else
        {
            ctx.Entry(existing).CurrentValues.SetValues(state);
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ResetSyncStateLastSyncAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var syncState = await ctx.TicketSyncStates.FindAsync([1], ct);
        if (syncState is null) return;
        syncState.LastSyncAt = null;
        await ctx.SaveChangesAsync(ct);
    }

    // ── User email lookup ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserEmailLookupEntry>> GetAllUserEmailLookupEntriesAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Set<UserEmail>()
            .AsNoTracking()
            .Select(ue => new UserEmailLookupEntry(ue.Email, ue.UserId, ue.IsOAuth))
            .ToListAsync(ct);
    }

    // ── TicketOrder reads (detached) ─────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, TicketOrder>> GetOrdersByVendorIdsAsync(
        IReadOnlyCollection<string> vendorOrderIds,
        CancellationToken ct = default)
    {
        if (vendorOrderIds.Count == 0)
            return new Dictionary<string, TicketOrder>(StringComparer.Ordinal);

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ids = vendorOrderIds.ToList();
        var orders = await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => ids.Contains(o.VendorOrderId))
            .ToListAsync(ct);
        return orders.ToDictionary(o => o.VendorOrderId, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, TicketAttendee>> GetAttendeesByVendorIdsAsync(
        IReadOnlyCollection<string> vendorTicketIds,
        CancellationToken ct = default)
    {
        if (vendorTicketIds.Count == 0)
            return new Dictionary<string, TicketAttendee>(StringComparer.Ordinal);

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ids = vendorTicketIds.ToList();
        var attendees = await ctx.TicketAttendees
            .AsNoTracking()
            .Where(a => ids.Contains(a.VendorTicketId))
            .ToListAsync(ct);
        return attendees.ToDictionary(a => a.VendorTicketId, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetOrderIdsByVendorIdAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.TicketOrders
            .AsNoTracking()
            .Select(o => new { o.VendorOrderId, o.Id })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.VendorOrderId, r => r.Id, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<TicketOrder>> GetOrdersNeedingStripeEnrichmentAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.StripePaymentIntentId != null && o.StripeFee == null)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketOrder>> GetAllOrdersWithAttendeesAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Include(o => o.Attendees)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OrderDiscountCodeRow>> GetOrderDiscountCodesAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.DiscountCode != null)
            .Select(o => new OrderDiscountCodeRow(o.DiscountCode!, o.PurchasedAt))
            .ToListAsync(ct);
    }

    // ── TicketAttendee reads (detached) ──────────────────────────────────────

    public async Task<IReadOnlyList<MatchedAttendeeRow>> GetMatchedAttendeesForEventAsync(
        string vendorEventId,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .Where(a => a.MatchedUserId != null && a.VendorEventId == vendorEventId)
            .Select(a => new MatchedAttendeeRow(a.MatchedUserId!.Value, a.Status))
            .ToListAsync(ct);
    }

    // ── TicketOrder / TicketAttendee writes ──────────────────────────────────

    public async Task UpsertOrdersAsync(
        IReadOnlyList<TicketOrder> orders,
        CancellationToken ct = default)
    {
        if (orders.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var vendorIds = orders.Select(o => o.VendorOrderId).ToList();
        var existing = await ctx.TicketOrders
            .Where(o => vendorIds.Contains(o.VendorOrderId))
            .ToDictionaryAsync(o => o.VendorOrderId, StringComparer.Ordinal, ct);

        foreach (var order in orders)
        {
            if (existing.TryGetValue(order.VendorOrderId, out var tracked))
            {
                // Copy mutable fields (Id, VendorOrderId are init-only).
                tracked.BuyerName = order.BuyerName;
                tracked.BuyerEmail = order.BuyerEmail;
                tracked.TotalAmount = order.TotalAmount;
                tracked.Currency = order.Currency;
                tracked.DiscountCode = order.DiscountCode;
                tracked.PaymentStatus = order.PaymentStatus;
                tracked.VendorEventId = order.VendorEventId;
                tracked.VendorDashboardUrl = order.VendorDashboardUrl;
                tracked.PurchasedAt = order.PurchasedAt;
                tracked.SyncedAt = order.SyncedAt;
                tracked.MatchedUserId = order.MatchedUserId;
                tracked.StripePaymentIntentId = order.StripePaymentIntentId;
                tracked.DiscountAmount = order.DiscountAmount;
                tracked.DonationAmount = order.DonationAmount;
            }
            else
            {
                ctx.TicketOrders.Add(order);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpsertAttendeesAsync(
        IReadOnlyList<TicketAttendee> attendees,
        CancellationToken ct = default)
    {
        if (attendees.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var vendorIds = attendees.Select(a => a.VendorTicketId).ToList();
        var existing = await ctx.TicketAttendees
            .Where(a => vendorIds.Contains(a.VendorTicketId))
            .ToDictionaryAsync(a => a.VendorTicketId, StringComparer.Ordinal, ct);

        foreach (var attendee in attendees)
        {
            if (existing.TryGetValue(attendee.VendorTicketId, out var tracked))
            {
                tracked.AttendeeName = attendee.AttendeeName;
                tracked.AttendeeEmail = attendee.AttendeeEmail;
                tracked.TicketTypeName = attendee.TicketTypeName;
                tracked.Price = attendee.Price;
                tracked.Status = attendee.Status;
                tracked.VendorEventId = attendee.VendorEventId;
                tracked.SyncedAt = attendee.SyncedAt;
                tracked.MatchedUserId = attendee.MatchedUserId;
                // TicketOrderId is init-only on existing rows; don't reparent.
            }
            else
            {
                ctx.TicketAttendees.Add(attendee);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateOrderVatAmountsAsync(
        IReadOnlyList<(Guid OrderId, decimal VatAmount)> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ids = updates.Select(u => u.OrderId).ToList();
        var tracked = await ctx.TicketOrders
            .Where(o => ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, ct);

        foreach (var (orderId, vat) in updates)
        {
            if (tracked.TryGetValue(orderId, out var order))
            {
                order.VatAmount = vat;
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateOrderStripeEnrichmentAsync(
        IReadOnlyList<TicketOrder> orders,
        CancellationToken ct = default)
    {
        if (orders.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var tracked = await ctx.TicketOrders
            .Where(o => ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, ct);

        foreach (var order in orders)
        {
            if (tracked.TryGetValue(order.Id, out var t))
            {
                t.PaymentMethod = order.PaymentMethod;
                t.PaymentMethodDetail = order.PaymentMethodDetail;
                t.StripeFee = order.StripeFee;
                t.ApplicationFee = order.ApplicationFee;
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
