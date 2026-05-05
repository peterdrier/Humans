using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Store;

/// <summary>
/// EF-backed implementation of <see cref="IStoreRepository"/>. The only
/// non-test file that touches <c>DbContext.StoreProducts</c>,
/// <c>DbContext.StoreOrders</c>, <c>DbContext.StoreOrderLines</c>,
/// <c>DbContext.StorePayments</c>, <c>DbContext.StoreInvoices</c>, or
/// <c>DbContext.StoreTreasurySyncStates</c>.
/// </summary>
/// <remarks>
/// Follows design-rules §15b: registered as Singleton, injects
/// <see cref="IDbContextFactory{TContext}"/>, and opens a fresh short-lived
/// <see cref="HumansDbContext"/> per method.
/// </remarks>
public sealed class StoreRepository : IStoreRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public StoreRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Products
    // ==========================================================================

    public async Task<IReadOnlyList<StoreProduct>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StoreProducts.AsNoTracking()
            .Where(p => p.Year == year && p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StoreProduct>> GetAllProductsForYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StoreProducts.AsNoTracking()
            .Where(p => p.Year == year)
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<StoreProduct?> GetProductByIdAsync(Guid productId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StoreProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetProductNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.StoreProducts.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    public async Task AddProductAsync(StoreProduct product, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StoreProducts.Add(product);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateProductAsync(StoreProduct product, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StoreProducts.Update(product);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Orders
    // ==========================================================================

    public async Task<IReadOnlyList<StoreOrder>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StoreOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .Where(o => o.CampSeasonId == campSeasonId)
            .ToListAsync(ct);
    }

    public async Task<StoreOrder?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StoreOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
    }

    public async Task<StoreOrder?> GetOrderWithLinesAndPaymentsAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StoreOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);
    }

    public async Task<IReadOnlyList<StoreOrder>> GetAllOrdersAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StoreOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .ToListAsync(ct);
    }

    public async Task AddOrderAsync(StoreOrder order, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StoreOrders.Add(order);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateOrderAsync(StoreOrder order, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StoreOrders.Update(order);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Lines
    // ==========================================================================

    public async Task AddLineAsync(StoreOrderLine line, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StoreOrderLines.Add(line);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RemoveLineAsync(Guid lineId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var line = await ctx.StoreOrderLines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return;
        ctx.StoreOrderLines.Remove(line);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<StoreLineContext?> GetLineWithOrderAndProductAsync(Guid lineId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.StoreOrderLines.AsNoTracking()
            .Where(l => l.Id == lineId)
            .Join(ctx.StoreOrders.AsNoTracking(),
                l => l.OrderId, o => o.Id,
                (l, o) => new { Line = l, Order = o })
            .Join(ctx.StoreProducts.AsNoTracking(),
                lo => lo.Line.ProductId, p => p.Id,
                (lo, p) => new StoreLineContext(
                    lo.Line.Id, lo.Order.Id, lo.Order.CampSeasonId, lo.Order.State, p.OrderableUntil))
            .FirstOrDefaultAsync(ct);
        return row;
    }

    // ==========================================================================
    // Payments
    // ==========================================================================

    public async Task AddPaymentAsync(StorePayment payment, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StorePayments.Add(payment);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.StorePayments.AnyAsync(p => p.StripePaymentIntentId == paymentIntentId, ct);
    }

    // ==========================================================================
    // Invoices
    // ==========================================================================

    public async Task AddInvoiceAsync(StoreInvoice invoice, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StoreInvoices.Add(invoice);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Treasury sync state
    // ==========================================================================

    public async Task<StoreTreasurySyncState> GetOrCreateTreasurySyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var s = await ctx.StoreTreasurySyncStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
        {
            s = new StoreTreasurySyncState { Id = 1 };
            ctx.StoreTreasurySyncStates.Add(s);
            await ctx.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task UpdateTreasurySyncStateAsync(StoreTreasurySyncState state, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.StoreTreasurySyncStates.Update(state);
        await ctx.SaveChangesAsync(ct);
    }
}
