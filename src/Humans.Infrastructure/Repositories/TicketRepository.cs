using System.Diagnostics.CodeAnalysis;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

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
    public async Task<int> CountValidAttendeesMatchedToUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .CountAsync(
                a => a.MatchedUserId == userId &&
                     (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn),
                ct);
    }

    [SuppressMessage("Meziantou", "MA0011:IFormatProvider is missing",
        Justification = "ToUpper() translates to SQL UPPER() in EF/Npgsql; no client-side format provider applies.")]
    public async Task<int> CountValidAttendeesByUppercaseEmailsAsync(
        IReadOnlyCollection<string> uppercaseEmails, CancellationToken ct = default)
    {
        if (uppercaseEmails.Count == 0)
            return 0;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
#pragma warning disable MA0011 // EF LINQ: ToUpper() translates to SQL upper()
        return await ctx.TicketAttendees
            .AsNoTracking()
            .CountAsync(
                a => a.AttendeeEmail != null &&
                     uppercaseEmails.Contains(a.AttendeeEmail.ToUpper()) &&
                     (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn),
                ct);
#pragma warning restore MA0011
    }

    public async Task<IReadOnlyList<Guid>> GetValidMatchedAttendeeUserIdsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .Where(a => a.MatchedUserId != null &&
                        (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn))
            .Select(a => a.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAllMatchedAttendeeUserIdsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .Where(a => a.MatchedUserId != null)
            .Select(a => a.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAllMatchedOrderUserIdsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.MatchedUserId != null)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetMatchedUserIdsForPaidOrdersAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId != null)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> HasAnyTicketMatchAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (await ctx.TicketAttendees.AsNoTracking().AnyAsync(a => a.MatchedUserId == userId, ct))
            return true;
        return await ctx.TicketOrders.AsNoTracking().AnyAsync(o => o.MatchedUserId == userId, ct);
    }

    public async Task<bool> HasEventTicketAsync(
        Guid userId, string vendorEventId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var hasPaidOrder = await ctx.TicketOrders
            .AsNoTracking()
            .AnyAsync(
                o => o.MatchedUserId == userId &&
                     o.VendorEventId == vendorEventId &&
                     o.PaymentStatus == TicketPaymentStatus.Paid, ct);
        if (hasPaidOrder)
            return true;

        return await ctx.TicketAttendees
            .AsNoTracking()
            .AnyAsync(
                a => a.MatchedUserId == userId &&
                     a.VendorEventId == vendorEventId &&
                     (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn), ct);
    }

    public async Task<IReadOnlyList<string>> GetDistinctTicketTypesAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .Select(a => a.TicketTypeName)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);
    }

    public async Task<int> CountSoldAttendeesAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .CountAsync(a =>
                a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn, ct);
    }

    // ==========================================================================
    // Reads — TicketOrders
    // ==========================================================================

    public async Task<IReadOnlyList<TicketOrder>> GetOrdersMatchedToUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Include(o => o.Attendees)
            .Where(o => o.MatchedUserId == userId)
            .OrderByDescending(o => o.PurchasedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketAttendee>> GetAttendeesMatchedToUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .Where(a => a.MatchedUserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(
        Instant fromInclusive,
        Instant toExclusive,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid
                        && o.PurchasedAt >= fromInclusive
                        && o.PurchasedAt < toExclusive)
            .Select(o => o.PurchasedAt)
            .ToListAsync(ct);
    }

    public async Task<TicketDashboardTotals> GetDashboardTotalsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ticketsSold = await ctx.TicketAttendees
            .AsNoTracking()
            .CountAsync(a =>
                a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn, ct);

        var paidOrders = ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid);

        var revenue = await paidOrders.SumAsync(o => o.TotalAmount, ct);
        var totalStripeFees = await paidOrders.SumAsync(o => o.StripeFee ?? 0m, ct);
        var totalAppFees = await paidOrders.SumAsync(o => o.ApplicationFee ?? 0m, ct);
        var unmatchedCount = await ctx.TicketOrders
            .AsNoTracking()
            .CountAsync(o => o.MatchedUserId == null, ct);

        return new TicketDashboardTotals
        {
            TicketsSold = ticketsSold,
            GrossRevenue = revenue,
            TotalStripeFees = totalStripeFees,
            TotalApplicationFees = totalAppFees,
            UnmatchedOrderCount = unmatchedCount,
        };
    }

    public async Task<IReadOnlyList<PaidOrderPaymentMethodRow>> GetPaidOrderPaymentMethodsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.PaymentMethod != null)
            .Select(o => new PaidOrderPaymentMethodRow
            {
                PaymentMethod = o.PaymentMethod,
                PaymentMethodDetail = o.PaymentMethodDetail,
                TotalAmount = o.TotalAmount,
                StripeFee = o.StripeFee,
                ApplicationFee = o.ApplicationFee,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OrderDateAndCount>> GetOrderDateAttendeeCountsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Select(o => new OrderDateAndCount
            {
                PurchasedAt = o.PurchasedAt,
                AttendeeCount = o.Attendees.Count,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RecentOrder>> GetRecentOrdersAsync(
        int count, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .OrderByDescending(o => o.PurchasedAt)
            .Take(count)
            .Select(o => new RecentOrder
            {
                Id = o.Id,
                BuyerName = o.BuyerName,
                TicketCount = o.Attendees.Count,
                Amount = o.TotalAmount,
                Currency = o.Currency,
                PurchasedAt = o.PurchasedAt,
                IsMatched = o.MatchedUserId != null,
            })
            .ToListAsync(ct);
    }

    public async Task<decimal> GetGrossPaidRevenueAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .SumAsync(o => o.TotalAmount, ct);
    }

    public async Task<IReadOnlyList<PaidOrderSalesRow>> GetPaidOrderSalesRowsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .Select(o => new PaidOrderSalesRow
            {
                PurchasedAt = o.PurchasedAt,
                TotalAmount = o.TotalAmount,
                DonationAmount = o.DonationAmount,
                VatAmount = o.VatAmount,
                AttendeeCount = o.Attendees.Count(a =>
                    a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn),
                VipDonations = o.Attendees
                    .Where(a =>
                        (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn) &&
                        a.Price > Humans.Domain.Constants.TicketConstants.VipThresholdEuros)
                    .Sum(a => a.Price - Humans.Domain.Constants.TicketConstants.VipThresholdEuros),
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DiscountCodeOrderRow>> GetOrdersWithDiscountCodesAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.DiscountCode != null)
            .Select(o => new DiscountCodeOrderRow
            {
                DiscountCode = o.DiscountCode!,
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                VendorOrderId = o.VendorOrderId,
            })
            .ToListAsync(ct);
    }

    [SuppressMessage("Meziantou", "MA0011:IFormatProvider is missing",
        Justification = "ToLower() translates to SQL lower() in EF/Npgsql for case-insensitive search.")]
    public async Task<(IReadOnlyList<OrderRow> Rows, int TotalCount)> GetOrdersPageAsync(
        string? search,
        string sortBy,
        bool sortDesc,
        int page,
        int pageSize,
        TicketPaymentStatus? filterPaymentStatus,
        string? filterTicketType,
        bool? filterMatched,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.TicketOrders.AsNoTracking().Include(o => o.Attendees).AsQueryable();

        if (HasSearchTerm(search, 1))
        {
            var normalizedSearch = search.ToLowerInvariant();
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            query = query.Where(o =>
                o.BuyerName.ToLower().Contains(normalizedSearch) ||
                o.BuyerEmail.ToLower().Contains(normalizedSearch) ||
                o.VendorOrderId.ToLower().Contains(normalizedSearch) ||
                (o.DiscountCode != null && o.DiscountCode.ToLower().Contains(normalizedSearch)));
#pragma warning restore MA0011
        }

        if (filterPaymentStatus is { } paymentStatus)
            query = query.Where(o => o.PaymentStatus == paymentStatus);

        if (!string.IsNullOrEmpty(filterTicketType))
            query = query.Where(o => o.Attendees.Any(a => a.TicketTypeName == filterTicketType));

        if (filterMatched == true)
            query = query.Where(o => o.MatchedUserId != null);
        else if (filterMatched == false)
            query = query.Where(o => o.MatchedUserId == null);

        var totalCount = await query.CountAsync(ct);

        query = ApplyOrderSorting(query, sortBy, sortDesc);

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrderRow
            {
                Id = o.Id,
                VendorOrderId = o.VendorOrderId,
                PurchasedAt = o.PurchasedAt,
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                AttendeeCount = o.Attendees.Count,
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                DiscountCode = o.DiscountCode,
                DiscountAmount = o.DiscountAmount,
                DonationAmount = o.DonationAmount,
                VatAmount = o.VatAmount,
                PaymentMethod = o.PaymentMethod,
                PaymentMethodDetail = o.PaymentMethodDetail,
                StripeFee = o.StripeFee,
                ApplicationFee = o.ApplicationFee,
                PaymentStatus = o.PaymentStatus,
                VendorDashboardUrl = o.VendorDashboardUrl,
                MatchedUserId = o.MatchedUserId,
                // MatchedUserName stitched by the service via IUserService
                MatchedUserName = null,
            })
            .ToListAsync(ct);

        return (rows, totalCount);
    }

    [SuppressMessage("Meziantou", "MA0011:IFormatProvider is missing",
        Justification = "ToLower() translates to SQL lower() in EF/Npgsql for case-insensitive search.")]
    public async Task<(IReadOnlyList<AttendeeRow> Rows, int TotalCount)> GetAttendeesPageAsync(
        string? search,
        string sortBy,
        bool sortDesc,
        int page,
        int pageSize,
        string? filterTicketType,
        TicketAttendeeStatus? filterStatus,
        bool? filterMatched,
        string? filterOrderId,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.TicketAttendees.AsNoTracking().Include(a => a.TicketOrder).AsQueryable();

        if (!string.IsNullOrEmpty(filterOrderId))
            query = query.Where(a => a.TicketOrder.VendorOrderId == filterOrderId);

        if (HasSearchTerm(search, 1))
        {
            var normalizedSearch = search.ToLowerInvariant();
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            query = query.Where(a =>
                a.AttendeeName.ToLower().Contains(normalizedSearch) ||
                (a.AttendeeEmail != null && a.AttendeeEmail.ToLower().Contains(normalizedSearch)));
#pragma warning restore MA0011
        }

        if (!string.IsNullOrEmpty(filterTicketType))
            query = query.Where(a => a.TicketTypeName == filterTicketType);

        if (filterStatus is { } status)
            query = query.Where(a => a.Status == status);

        if (filterMatched == true)
            query = query.Where(a => a.MatchedUserId != null);
        else if (filterMatched == false)
            query = query.Where(a => a.MatchedUserId == null);

        var totalCount = await query.CountAsync(ct);

        query = ApplyAttendeeSorting(query, sortBy, sortDesc);

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AttendeeRow
            {
                Id = a.Id,
                AttendeeName = a.AttendeeName,
                AttendeeEmail = a.AttendeeEmail,
                TicketTypeName = a.TicketTypeName,
                Price = a.Price,
                Status = a.Status,
                MatchedUserId = a.MatchedUserId,
                // MatchedUserName stitched by the service via IUserService
                MatchedUserName = null,
                VendorOrderId = a.TicketOrder.VendorOrderId,
            })
            .ToListAsync(ct);

        return (rows, totalCount);
    }

    public async Task<IReadOnlyList<AttendeeExportRow>> GetAttendeeExportDataAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketAttendees
            .AsNoTracking()
            .Include(a => a.TicketOrder)
            .OrderBy(a => a.AttendeeName)
            .Select(a => new AttendeeExportRow
            {
                AttendeeName = a.AttendeeName,
                AttendeeEmail = a.AttendeeEmail,
                TicketTypeName = a.TicketTypeName,
                Price = a.Price,
                Status = a.Status.ToString(),
                VendorOrderId = a.TicketOrder.VendorOrderId,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OrderExportRow>> GetOrderExportDataAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var orders = await ctx.TicketOrders
            .AsNoTracking()
            .Include(o => o.Attendees)
            .OrderByDescending(o => o.PurchasedAt)
            .ToListAsync(ct);

        return orders.Select(o =>
        {
            var method = o.PaymentMethodDetail != null
                ? $"{o.PaymentMethod}/{o.PaymentMethodDetail}"
                : o.PaymentMethod;
            return new OrderExportRow
            {
                Date = o.PurchasedAt.InUtc().Date.ToIsoDateString(),
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                AttendeeCount = o.Attendees.Count,
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                DiscountCode = o.DiscountCode,
                DiscountAmount = o.DiscountAmount,
                DonationAmount = o.DonationAmount,
                VatAmount = o.VatAmount,
                PaymentMethod = method,
                StripeFee = o.StripeFee,
                ApplicationFee = o.ApplicationFee,
                PaymentStatus = o.PaymentStatus.ToString(),
            };
        }).ToList();
    }

    // ==========================================================================
    // Reads — TicketSyncState
    // ==========================================================================

    // ==========================================================================
    // Writes — TicketSyncState (crash-recovery reset)
    // ==========================================================================

    public async Task<TicketSyncState?> ResetStaleRunningStateAsync(
        Instant olderThan,
        Instant now,
        string errorMessage,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var state = await ctx.TicketSyncStates.FindAsync([1], ct);
        if (state is null)
            return null;

        if (state.SyncStatus == TicketSyncStatus.Running
            && state.StatusChangedAt is { } changedAt
            && changedAt <= olderThan)
        {
            state.SyncStatus = TicketSyncStatus.Error;
            state.LastError = errorMessage;
            state.StatusChangedAt = now;
            await ctx.SaveChangesAsync(ct);
        }

        ctx.Entry(state).State = EntityState.Detached;
        return state;
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static bool HasSearchTerm(
        [NotNullWhen(true)] string? value, int minLength = 2) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= minLength;

    private static IQueryable<TicketOrder> ApplyOrderSorting(
        IQueryable<TicketOrder> query, string? sortBy, bool sortDesc)
    {
        if (string.Equals(sortBy, "amount", StringComparison.OrdinalIgnoreCase))
            return sortDesc ? query.OrderByDescending(o => o.TotalAmount) : query.OrderBy(o => o.TotalAmount);

        if (string.Equals(sortBy, "name", StringComparison.OrdinalIgnoreCase))
            return sortDesc ? query.OrderByDescending(o => o.BuyerName) : query.OrderBy(o => o.BuyerName);

        if (string.Equals(sortBy, "tickets", StringComparison.OrdinalIgnoreCase))
            return sortDesc ? query.OrderByDescending(o => o.Attendees.Count) : query.OrderBy(o => o.Attendees.Count);

        return sortDesc ? query.OrderByDescending(o => o.PurchasedAt) : query.OrderBy(o => o.PurchasedAt);
    }

    private static IQueryable<TicketAttendee> ApplyAttendeeSorting(
        IQueryable<TicketAttendee> query, string? sortBy, bool sortDesc)
    {
        if (string.Equals(sortBy, "type", StringComparison.OrdinalIgnoreCase))
            return sortDesc ? query.OrderByDescending(a => a.TicketTypeName) : query.OrderBy(a => a.TicketTypeName);

        if (string.Equals(sortBy, "price", StringComparison.OrdinalIgnoreCase))
            return sortDesc ? query.OrderByDescending(a => a.Price) : query.OrderBy(a => a.Price);

        if (string.Equals(sortBy, "status", StringComparison.OrdinalIgnoreCase))
            return sortDesc ? query.OrderByDescending(a => a.Status) : query.OrderBy(a => a.Status);

        return sortDesc ? query.OrderByDescending(a => a.AttendeeName) : query.OrderBy(a => a.AttendeeName);
    }
}
