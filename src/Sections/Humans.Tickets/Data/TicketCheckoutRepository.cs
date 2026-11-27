using Humans.Tickets.Domain.Checkout;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Humans.Tickets.Data;

internal sealed class TicketCheckoutRepository(IDbContextFactory<TicketsDbContext> factory) : ITicketCheckoutRepository
{
    public async Task<IReadOnlyList<TicketingSettings>> ListTicketingSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketingSettings.AsNoTracking().ToListAsync(ct);
    }

    public async Task<TicketingSettings?> GetTicketingSettingsAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketingSettings.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<TicketingSettings?> GetTicketingSettingsForEventAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketingSettings.AsNoTracking().FirstOrDefaultAsync(e => e.EventSettingsId == eventSettingsId, ct);
    }

    public async Task SaveTicketingSettingsAsync(TicketingSettings ticketing, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.TicketingSettings.FirstOrDefaultAsync(e => e.Id == ticketing.Id, ct);
        if (tracked is null)
        {
            ctx.TicketingSettings.Add(ticketing);
        }
        else
        {
            // Owned JSON lists carry shadow ordinal keys, so a detached graph cannot be attached with Update().
            ctx.Entry(tracked).CurrentValues.SetValues(ticketing);
            tracked.Waves = ticketing.Waves.Select(w => new TicketSalesWave { Name = w.Name, EndsOn = w.EndsOn, PriceMinor = w.PriceMinor }).ToList();
            tracked.VipTotalsMinor = ticketing.VipTotalsMinor.ToList();
            tracked.DonationPurposes = ticketing.DonationPurposes?.ToList();
            tracked.ChildTypes = ticketing.ChildTypes
                .Select(c => new TicketChildType { Name = c.Name, UnderAge = c.UnderAge, PriceMinor = c.PriceMinor }).ToList();
            tracked.PriceRules = ticketing.PriceRules.Select(r => new TicketPriceRule
            {
                GrantProgramId = r.GrantProgramId,
                Kind = r.Kind,
                AmountMinor = r.AmountMinor,
                Label = r.Label,
            }).ToList();
            tracked.Switches = ticketing.Switches is { } sw
                ? new TicketingSwitches
                {
                    Resale = sw.Resale,
                    PartnerTickets = sw.PartnerTickets,
                    Donations = sw.Donations,
                    MailingListOptIn = sw.MailingListOptIn,
                    CompanionTickets = sw.CompanionTickets,
                }
                : null;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<TicketCheckoutOrder?> GetOrderAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
    }

    public async Task<TicketCheckoutOrder?> GetOrderByPlatformIdAsync(
        string platformKey, string platformOrderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.PlatformKey == platformKey && o.PlatformOrderId == platformOrderId, ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSettingsAsync(
        Guid settingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking()
            .Where(o => o.TicketingSettingsId == settingsId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default, IReadOnlyCollection<string>? emails = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Ticket lines live in a JSON column; the whole table fits in memory, so filter holders there.
        var orders = await ctx.TicketCheckoutOrders.AsNoTracking().ToListAsync(ct);
        return orders
            .Where(o => o.BuyerUserId == userId || o.Lines.Any(l => l.HolderUserId == userId
                || (l.HolderUserId is null && l.HolderEmail is { } email && emails?.Contains(email, StringComparer.OrdinalIgnoreCase) == true)))
            .ToList();
    }

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutOrders.AsNoTracking()
            .Where(o => o.State == TicketCheckoutOrderState.Created || o.State == TicketCheckoutOrderState.Pending
                || o.State == TicketCheckoutOrderState.Processing)
            .ToListAsync(ct);
    }

    public async Task AddOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default, TicketCheckoutTicket? ticket = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCheckoutOrders.Add(order);
        if (ticket is not null)
            ctx.TicketCheckoutTickets.Add(ticket);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default, TicketCheckoutTicket? ticket = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.TicketCheckoutOrders.FirstAsync(o => o.Id == order.Id, ct);
        // Owned JSON lines carry shadow ordinal keys, so a detached graph cannot be attached with Update().
        ctx.Entry(tracked).CurrentValues.SetValues(order);
        tracked.Lines = order.Lines.Select(CopyLine).ToList();
        if (ticket is not null)
            ctx.TicketCheckoutTickets.Update(ticket);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutRefunds.AsNoTracking()
            .Where(r => r.OrderId == orderId)
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

    public async Task<bool> AddTicketsAsync(IReadOnlyList<TicketCheckoutTicket> tickets, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCheckoutTickets.AddRange(tickets);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    public async Task<bool> TryClaimPaidAsync(Guid orderId, Instant paidAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var claimed = await ctx.TicketCheckoutOrders
            .Where(o => o.Id == orderId && o.State != TicketCheckoutOrderState.Paid)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.State, TicketCheckoutOrderState.Paid)
                .SetProperty(o => o.PaidAt, paidAt), ct);
        return claimed == 1;
    }

    public async Task UpdateTicketAsync(TicketCheckoutTicket ticket, CancellationToken ct = default,
        IReadOnlyList<TicketCheckoutTicket>? dependents = null, TicketResaleOffer? offer = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCheckoutTickets.Update(ticket);
        if (dependents is not null)
            ctx.TicketCheckoutTickets.UpdateRange(dependents);
        if (offer is not null)
            CopyOffer(ctx, await ctx.TicketResaleOffers.FirstAsync(o => o.Id == offer.Id, ct), offer);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<TicketCheckoutTicket?> GetTicketByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Barcode == barcode, ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutTickets.AsNoTracking().Where(t => t.OrderId == orderId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default, IReadOnlyCollection<string>? emails = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var candidates = await ctx.TicketCheckoutTickets.AsNoTracking()
            .Where(t => t.HolderUserId == userId || (emails != null && t.HolderUserId == null && t.HolderEmail != null))
            .ToListAsync(ct);
        return candidates.Where(t => t.HolderUserId == userId
            || (t.HolderEmail is { } email && emails?.Contains(email, StringComparer.OrdinalIgnoreCase) == true)).ToList();
    }

    public async Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSettingsAsync(Guid settingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutTickets.AsNoTracking().Where(t => t.TicketingSettingsId == settingsId).ToListAsync(ct);
    }

    public async Task AddOfferAsync(TicketResaleOffer offer, CancellationToken ct = default, TicketCheckoutTicket? ticket = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketResaleOffers.Add(offer);
        if (ticket is not null)
            ctx.TicketCheckoutTickets.Update(ticket);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateOfferAsync(TicketResaleOffer offer, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.TicketResaleOffers.FirstAsync(o => o.Id == offer.Id, ct);
        CopyOffer(ctx, tracked, offer);
        await ctx.SaveChangesAsync(ct);
    }

    private static void CopyOffer(TicketsDbContext ctx, TicketResaleOffer tracked, TicketResaleOffer offer)
    {
        // Owned JSON invites carry shadow ordinal keys, so a detached graph cannot be attached with Update().
        ctx.Entry(tracked).CurrentValues.SetValues(offer);
        tracked.Invites = offer.Invites
            .Select(i => new TicketResaleInvite
            {
                Email = i.Email,
                TokenHash = i.TokenHash,
                SentAt = i.SentAt,
                DeliveryId = i.DeliveryId,
                DeliveryPending = i.DeliveryPending,
            }).ToList();
    }

    public async Task<TicketResaleOffer?> GetOfferAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketResaleOffers.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
    }

    public async Task<TicketResaleOffer?> GetOfferByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return (await ctx.TicketResaleOffers.AsNoTracking().ToListAsync(ct)).FirstOrDefault(o => o.InviteFor(tokenHash) is not null);
    }

    public async Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSettingsAsync(Guid settingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketResaleOffers.AsNoTracking().Where(o => o.TicketingSettingsId == settingsId).ToListAsync(ct);
    }

    public async Task AddCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCompanionRequests.Add(request);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketCompanionRequests.Update(request);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<TicketCompanionRequest?> GetCompanionRequestAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCompanionRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForSettingsAsync(Guid settingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCompanionRequests.AsNoTracking().Where(r => r.TicketingSettingsId == settingsId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCompanionRequests.AsNoTracking()
            .Where(r => r.HolderUserId == userId || r.CompanionUserId == userId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSettingsAsync(Guid settingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketCheckoutRefunds.AsNoTracking()
            .Where(r => ctx.TicketCheckoutOrders.Any(o => o.Id == r.OrderId && o.TicketingSettingsId == settingsId))
            .ToListAsync(ct);
    }

    public async Task<int> ErasePiiForUserAsync(Guid userId, IReadOnlyCollection<string> emails, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var addresses = new HashSet<string>(emails, StringComparer.OrdinalIgnoreCase);
        // A line or ticket without an account (a companion the team issued by email) is theirs by its email alone.
        bool Theirs(Guid? holder, string? email) =>
            holder == userId || (holder is null && email is not null && addresses.Contains(email));
        var held = (await ctx.TicketCheckoutTickets.Where(t => t.HolderUserId == userId || (t.HolderUserId == null && t.HolderEmail != null))
                .ToListAsync(ct))
            .Where(t => Theirs(t.HolderUserId, t.HolderEmail))
            .ToList();
        var heldIds = held.Select(t => t.Id).ToList();
        var children = await ctx.TicketCheckoutTickets
            .Where(t => t.ParentTicketId != null && heldIds.Contains(t.ParentTicketId.Value) && t.HolderUserId == null)
            .ToListAsync(ct);
        // A dependent without an account may sit on an order of its own, such as a companion the ticketing team issued.
        var childLines = children.Select(t => (t.OrderId, t.LineIndex)).ToHashSet();
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
            var own = order.Lines.Select((l, i) => (l, i))
                .Where(x => Theirs(x.l.HolderUserId, x.l.HolderEmail) || childLines.Contains((order.Id, x.i)))
                .Select(x => x.i).ToHashSet();
            foreach (var line in order.Lines.Where((l, i) => own.Contains(i) || l.ParentLineIndex is { } parent && own.Contains(parent)))
            {
                line.HolderName = TicketPiiTombstone.Name;
                line.HolderEmail = line.HolderEmail is null ? null : TicketPiiTombstone.EmailFor(userId);
                touched = true;
            }
            if (!touched) continue;
            // Owned JSON lists are only rewritten when EF sees the owner as changed.
            ctx.TicketCheckoutOrders.Update(order);
            changed++;
        }
        foreach (var ticket in held.Concat(children))
        {
            ticket.HolderName = TicketPiiTombstone.Name;
            ticket.HolderEmail = ticket.HolderEmail is null ? null : TicketPiiTombstone.EmailFor(userId);
            ticket.VoidRefundId = null;
            ticket.VoidRefundMinor = null;
            ticket.VoidRequestedReason = null;
            changed++;
        }
        foreach (var offer in (await ctx.TicketResaleOffers.ToListAsync(ct))
                     .Where(o => (o.HolderUserId == userId && (o.Invites.Count > 0 || o.SoldNotificationPending is not null))
                                 || o.Invites.Any(i => addresses.Contains(i.Email))))
        {
            offer.Invites = offer.HolderUserId == userId ? [] : offer.Invites.Where(i => !addresses.Contains(i.Email)).ToList();
            if (offer.HolderUserId == userId)
                offer.SoldNotificationPending = null;
            ctx.TicketResaleOffers.Update(offer);
            changed++;
        }
        foreach (var request in await ctx.TicketCompanionRequests
                     .Where(r => (r.HolderUserId == userId || r.CompanionUserId == userId) && r.Note != TicketPiiTombstone.Name)
                     .ToListAsync(ct))
        {
            request.Note = TicketPiiTombstone.Name;
            request.DecisionNote = null;
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
            foreach (var line in order.Lines.Where(l => l.HolderUserId == fromUserId))
            {
                line.HolderUserId = toUserId;
                touched = true;
            }
            if (touched) ctx.TicketCheckoutOrders.Update(order);
        }
        foreach (var orderEvent in await ctx.TicketCheckoutOrderEvents.Where(e => e.ActorUserId == fromUserId).ToListAsync(ct))
            orderEvent.ActorUserId = toUserId;
        foreach (var ticket in await ctx.TicketCheckoutTickets.Where(t => t.HolderUserId == fromUserId).ToListAsync(ct))
            ticket.HolderUserId = toUserId;
        foreach (var offer in await ctx.TicketResaleOffers.Where(o => o.HolderUserId == fromUserId).ToListAsync(ct))
            offer.HolderUserId = toUserId;
        foreach (var request in await ctx.TicketCompanionRequests
                     .Where(r => r.HolderUserId == fromUserId || r.CompanionUserId == fromUserId || r.DecidedByUserId == fromUserId)
                     .ToListAsync(ct))
        {
            if (request.HolderUserId == fromUserId) request.HolderUserId = toUserId;
            if (request.CompanionUserId == fromUserId) request.CompanionUserId = toUserId;
            if (request.DecidedByUserId == fromUserId) request.DecidedByUserId = toUserId;
        }
        await ctx.SaveChangesAsync(ct);
    }

    private static TicketCheckoutOrderLine CopyLine(TicketCheckoutOrderLine l) => new()
    {
        Kind = l.Kind,
        Description = l.Description,
        AmountMinor = l.AmountMinor,
        VatRate = l.VatRate,
        HolderName = l.HolderName,
        HolderEmail = l.HolderEmail,
        HolderUserId = l.HolderUserId,
        HolderKind = l.HolderKind,
        ParentLineIndex = l.ParentLineIndex,
        WaveName = l.WaveName,
        ClassProgramId = l.ClassProgramId,
        ClassLabel = l.ClassLabel,
        DiscountsMinor = l.DiscountsMinor,
        ResaleOfferId = l.ResaleOfferId,
        DonationPurpose = l.DonationPurpose,
    };
}
