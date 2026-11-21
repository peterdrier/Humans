using Humans.Tickets.Domain.Checkout;
using Microsoft.EntityFrameworkCore;

namespace Humans.Tickets.Data;

internal sealed class TicketCheckoutRepository(IDbContextFactory<TicketsDbContext> factory) : ITicketCheckoutRepository
{
    public async Task<IReadOnlyList<TicketSalesEvent>> GetSalesEventsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketSalesEvents.AsNoTracking().ToListAsync(ct);
    }

    public async Task<TicketSalesEvent?> GetSalesEventAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketSalesEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<TicketSalesEvent?> GetSalesEventForEventAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketSalesEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventSettingsId == eventSettingsId, ct);
    }

    public async Task SaveSalesEventAsync(TicketSalesEvent salesEvent, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.TicketSalesEvents.FirstOrDefaultAsync(e => e.Id == salesEvent.Id, ct);
        if (tracked is null)
        {
            ctx.TicketSalesEvents.Add(salesEvent);
        }
        else
        {
            // Owned JSON lists carry shadow ordinal keys, so a detached graph cannot be attached with Update().
            ctx.Entry(tracked).CurrentValues.SetValues(salesEvent);
            tracked.Waves = salesEvent.Waves.Select(w => new TicketSalesWave { Name = w.Name, EndsOn = w.EndsOn, PriceMinor = w.PriceMinor }).ToList();
            tracked.SupporterTotalsMinor = salesEvent.SupporterTotalsMinor.ToList();
            tracked.PriceRules = salesEvent.PriceRules.Select(r => new TicketPriceRule
            {
                GrantProgramId = r.GrantProgramId,
                Kind = r.Kind,
                AmountMinor = r.AmountMinor,
                Label = r.Label,
            }).ToList();
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<TicketCheckoutOrder?> GetOrderAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
    }

    public async Task<TicketCheckoutOrder?> GetOrderByBackendIdAsync(
        string backendKey, string backendOrderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.BackendKey == backendKey && o.BackendOrderId == backendOrderId, ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSalesEventAsync(
        Guid salesEventId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking()
            .Where(o => o.SalesEventId == salesEventId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Ticket lines live in a JSON column; the whole table fits in memory, so filter holders there.
        var orders = await ctx.TicketCheckoutOrders.AsNoTracking().ToListAsync(ct);
        return orders
            .Where(o => o.BuyerUserId == userId || o.Lines.Any(l => l.AttendeeUserId == userId))
            .ToList();
    }

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking()
            .Where(o => o.State == TicketCheckoutOrderState.Created || o.State == TicketCheckoutOrderState.Pending)
            .ToListAsync(ct);
    }

    public async Task AddOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCheckoutOrders.Add(order);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.TicketCheckoutOrders.FirstAsync(o => o.Id == order.Id, ct);
        // Owned JSON lines carry shadow ordinal keys, so a detached graph cannot be attached with Update().
        ctx.Entry(tracked).CurrentValues.SetValues(order);
        tracked.Lines = order.Lines.Select(CopyLine).ToList();
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutRefunds.AsNoTracking()
            .Where(r => r.OrderId == orderId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSalesEventAsync(
        Guid salesEventId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutRefunds.AsNoTracking()
            .Where(r => ctx.TicketCheckoutOrders.Any(o => o.Id == r.OrderId && o.SalesEventId == salesEventId))
            .ToListAsync(ct);
    }

    public async Task AddRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCheckoutRefunds.Add(refund);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCheckoutRefunds.Update(refund);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddOrderEventAsync(TicketCheckoutOrderEvent orderEvent, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCheckoutOrderEvents.Add(orderEvent);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutOrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrderEvents.AsNoTracking()
            .Where(e => e.OrderId == orderId)
            .ToListAsync(ct);
    }

    public async Task<int> ErasePiiForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var orders = await ctx.TicketCheckoutOrders.ToListAsync(ct);
        var changed = 0;
        foreach (var order in orders)
        {
            var touched = false;
            if (order.BuyerUserId == userId)
            {
                order.BuyerName = TicketPiiTombstone.Name;
                order.BuyerEmail = TicketPiiTombstone.EmailFor(userId);
                touched = true;
            }
            foreach (var line in order.Lines.Where(l => l.AttendeeUserId == userId))
            {
                line.AttendeeName = TicketPiiTombstone.Name;
                line.AttendeeEmail = TicketPiiTombstone.EmailFor(userId);
                touched = true;
            }
            if (!touched) continue;
            // Owned JSON lists are only rewritten when EF sees the owner as changed.
            ctx.TicketCheckoutOrders.Update(order);
            changed++;
        }
        await ctx.SaveChangesAsync(ct);
        return changed;
    }

    public async Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var orders = await ctx.TicketCheckoutOrders.ToListAsync(ct);
        foreach (var order in orders)
        {
            var touched = false;
            if (order.BuyerUserId == fromUserId)
            {
                ctx.Entry(order).Property(nameof(TicketCheckoutOrder.BuyerUserId)).CurrentValue = toUserId;
                touched = true;
            }
            foreach (var line in order.Lines.Where(l => l.AttendeeUserId == fromUserId))
            {
                line.AttendeeUserId = toUserId;
                touched = true;
            }
            if (touched) ctx.TicketCheckoutOrders.Update(order);
        }
        foreach (var orderEvent in await ctx.TicketCheckoutOrderEvents.Where(e => e.ActorUserId == fromUserId).ToListAsync(ct))
            orderEvent.ActorUserId = toUserId;
        await ctx.SaveChangesAsync(ct);
    }

    private static TicketCheckoutOrderLine CopyLine(TicketCheckoutOrderLine l) => new()
    {
        Kind = l.Kind,
        Description = l.Description,
        AmountMinor = l.AmountMinor,
        VatRate = l.VatRate,
        AttendeeName = l.AttendeeName,
        AttendeeEmail = l.AttendeeEmail,
        AttendeeUserId = l.AttendeeUserId,
        Barcode = l.Barcode,
        DonationPurpose = l.DonationPurpose,
    };
}
