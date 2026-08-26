using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Store.Data;

/// <summary>
/// EF-backed implementation of <see cref="IStoreRepository"/>. The only
/// non-test file that touches <c>DbContext.Products</c>,
/// <c>DbContext.Orders</c>, <c>DbContext.OrderLines</c>,
/// <c>DbContext.Payments</c>, or <c>DbContext.Invoices</c>.
/// Nothing reads or writes <c>DbContext.TreasurySyncStates</c>; the table
/// ships unused (see <c>Docs/Store.md</c>, treasury sync).
/// </summary>
/// <remarks>
/// Follows design-rules §15b: registered as Singleton, injects
/// <see cref="IDbContextFactory{TContext}"/>, and opens a fresh short-lived
/// <see cref="StoreDbContext"/> per method.
/// </remarks>
internal sealed class Repository(IDbContextFactory<StoreDbContext> factory) : IStoreRepository
{
    // ==========================================================================
    // Products
    // ==========================================================================

    public async Task<IReadOnlyList<Product>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Products.AsNoTracking()
            .Where(p => p.Year == year && p.IsActive)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Product>> GetAllProductsForYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Products.AsNoTracking()
            .Where(p => p.Year == year)
            .ToListAsync(ct);
    }

    public async Task<Product?> GetProductByIdAsync(Guid productId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
    }

    public async Task<IReadOnlyList<Product>> GetProductsByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);
    }

    public async Task AddProductAsync(Product product, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateProductAsync(Product product, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Products.Update(product);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Orders
    // ==========================================================================

    public async Task<IReadOnlyList<Order>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .Where(o => o.CampSeasonId == campSeasonId)
            .ToListAsync(ct);
    }

    public async Task<Order?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
    }

    public async Task<Order?> GetOrderWithLinesAndPaymentsAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);
    }

    public async Task<IReadOnlyList<Order>> GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
        IReadOnlyCollection<Guid> campSeasonIds,
        CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
            return Array.Empty<Order>();

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking()
            .Where(o => o.CampSeasonId.HasValue && campSeasonIds.Contains(o.CampSeasonId.Value))
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .ToListAsync(ct);
    }

    public async Task<Order?> GetOrderForTeamAsync(Guid teamId, int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .Where(o => o.TeamId == teamId && o.Year == year)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Order>> GetOrdersForTeamsWithLinesAsync(
        IReadOnlyCollection<Guid> teamIds,
        int year,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return Array.Empty<Order>();

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking()
            .Where(o => o.TeamId.HasValue && teamIds.Contains(o.TeamId.Value) && o.Year == year)
            .Include(o => o.Lines)
            .ToListAsync(ct);
    }

    public async Task AddOrderAsync(Order order, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateOrderAsync(Order order, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Orders.Update(order);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var order = await ctx.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return;
        ctx.Orders.Remove(order);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Lines
    // ==========================================================================

    public async Task AddLineAsync(OrderLine line, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.OrderLines.Add(line);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RemoveLineAsync(Guid lineId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var line = await ctx.OrderLines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return;
        ctx.OrderLines.Remove(line);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<LineContext?> GetLineWithOrderAndProductAsync(Guid lineId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.OrderLines.AsNoTracking()
            .Where(l => l.Id == lineId)
            .Join(ctx.Orders.AsNoTracking(),
                l => l.OrderId, o => o.Id,
                (l, o) => new { Line = l, Order = o })
            .Join(ctx.Products.AsNoTracking(),
                lo => lo.Line.ProductId, p => p.Id,
                (lo, p) => new LineContext(
                    lo.Line.Id, lo.Order.Id, lo.Order.CampSeasonId, lo.Order.State, p.OrderableUntil))
            .FirstOrDefaultAsync(ct);
        return row;
    }

    // ==========================================================================
    // Payments
    // ==========================================================================

    public async Task AddPaymentAsync(Payment payment, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Payments.AnyAsync(p => p.StripePaymentIntentId == paymentIntentId, ct);
    }

    public async Task<Payment?> GetPaymentByStripePaymentIntentIdAsync(string paymentIntentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.StripePaymentIntentId == paymentIntentId, ct);
    }

    public async Task UpdatePaymentStatusAsync(Guid paymentId, PaymentStatus status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var payment = await ctx.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return;
        payment.Status = status;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeletePaymentAsync(Guid paymentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var payment = await ctx.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return;
        ctx.Payments.Remove(payment);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RecordedStripePayment>> GetRecordedStripePaymentsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Payments.AsNoTracking()
            .Where(p => p.StripePaymentIntentId != null)
            .Select(p => new RecordedStripePayment(
                p.StripePaymentIntentId!, p.OrderId, p.AmountEur, p.ReceivedAt, p.Status))
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Invoices
    // ==========================================================================

    public async Task SaveIssuedInvoiceAsync(Invoice invoice, Order order, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Invoices.Add(invoice);

        // The order arrives detached (AsNoTracking + Includes) from a read taken before several
        // Holded round-trips, so its Payments are stale — a Stripe webhook may have settled one in
        // the meantime. Update(order) would mark that whole graph modified and write the stale
        // payment rows back, and there is no concurrency token to catch it
        // (memory/architecture/no-concurrency-tokens.md). So attach the aggregate and mark only the
        // columns issuance owns: the state flip, and the repriced line snapshots.
        ctx.Orders.Attach(order);
        var orderEntry = ctx.Entry(order);
        orderEntry.Property(o => o.State).IsModified = true;
        orderEntry.Property(o => o.IssuedInvoiceId).IsModified = true;
        orderEntry.Property(o => o.UpdatedAt).IsModified = true;
        foreach (var line in order.Lines)
        {
            var lineEntry = ctx.Entry(line);
            lineEntry.Property(l => l.UnitPriceSnapshot).IsModified = true;
            lineEntry.Property(l => l.VatRateSnapshot).IsModified = true;
            lineEntry.Property(l => l.DepositAmountSnapshot).IsModified = true;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
