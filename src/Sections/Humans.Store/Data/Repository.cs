using Humans.Base.Data;
using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Store.Data;

internal sealed class Repository(IDbContextFactory<StoreDbContext> factory) : SectionRepository<StoreDbContext>(factory), IStoreRepository
{
    public async Task<IReadOnlyList<Product>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Products.AsNoTracking()
            .Where(p => p.Year == year && p.IsActive)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Product>> GetAllProductsForYearAsync(int year, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Products.AsNoTracking()
            .Where(p => p.Year == year)
            .ToListAsync(ct), ct);

    public async Task<Product?> GetProductByIdAsync(Guid productId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct), ct);

    public async Task<IReadOnlyList<Product>> GetProductsByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);
    }

    public async Task AddProductAsync(Product product, CancellationToken ct = default) =>
        await InsertAsync(product, ct);

    public async Task UpdateProductAsync(Product product, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.Products.Update(product);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Order>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .Where(o => o.CampSeasonId == campSeasonId)
            .ToListAsync(ct), ct);

    public async Task<Order?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct), ct);

    public async Task<Order?> GetOrderWithLinesAsync(Guid orderId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct), ct);

    public async Task<IReadOnlyList<Order>> GetOrdersWithMissingYearAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Orders.AsNoTracking()
            .Where(o => o.Year == 0)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Order>> GetOrdersForCampSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds,
        CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
            return Array.Empty<Order>();

        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking()
            .Where(o => o.CampSeasonId.HasValue && campSeasonIds.Contains(o.CampSeasonId.Value))
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .ToListAsync(ct);
    }

    public async Task<Order?> GetOrderForTeamAsync(Guid teamId, int year, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .Where(o => o.TeamId == teamId && o.Year == year)
            .FirstOrDefaultAsync(ct), ct);

    public async Task<IReadOnlyList<Order>> GetOrdersForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        int year,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return Array.Empty<Order>();

        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.Orders.AsNoTracking()
            .Where(o => o.TeamId.HasValue && teamIds.Contains(o.TeamId.Value) && o.Year == year)
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Order>> GetOrdersForYearAsync(
        int year,
        IReadOnlyCollection<Guid> campSeasonIds,
        CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Orders.AsNoTracking()
            .Where(o => o.Year == year
                || (o.Year == 0 && o.CampSeasonId.HasValue && campSeasonIds.Contains(o.CampSeasonId.Value)))
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .ToListAsync(ct), ct);

    public async Task AddOrderAsync(Order order, CancellationToken ct = default) =>
        await InsertAsync(order, ct);

    public async Task UpdateOrderAsync(Order order, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.Orders.Update(order);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var order = await ctx.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return;
        ctx.Orders.Remove(order);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddLineAsync(OrderLine line, CancellationToken ct = default) =>
        await InsertAsync(line, ct);

    public async Task RemoveLineAsync(Guid lineId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var line = await ctx.OrderLines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return;
        ctx.OrderLines.Remove(line);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<LineContext?> GetLineAsync(Guid lineId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.OrderLines.AsNoTracking()
            .Where(l => l.Id == lineId)
            .Join(ctx.Orders.AsNoTracking(),
                l => l.OrderId, o => o.Id,
                (l, o) => new { Line = l, Order = o })
            .Join(ctx.Products.AsNoTracking(),
                lo => lo.Line.ProductId, p => p.Id,
                (lo, p) => new LineContext(
                    lo.Order.Id, lo.Order.State, p.OrderableUntil,
                    p.Name, lo.Line.Qty, lo.Line.UnitPriceSnapshot))
            .FirstOrDefaultAsync(ct);
        return row;
    }

    public async Task AddPaymentAsync(Payment payment, CancellationToken ct = default) =>
        await InsertAsync(payment, ct);

    public async Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Payments.AnyAsync(p => p.StripePaymentIntentId == paymentIntentId, ct), ct);

    public async Task<Payment?> GetPaymentByIntentIdAsync(string paymentIntentId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.StripePaymentIntentId == paymentIntentId, ct), ct);

    public async Task UpdatePaymentStatusAsync(Guid paymentId, PaymentStatus status, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var payment = await ctx.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return;
        payment.Status = status;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Payment>> GetPaymentsMissingMethodNameAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Payments.AsNoTracking()
            .Where(p => p.MethodName == null)
            .ToListAsync(ct), ct);

    public async Task SetPaymentMethodNameAsync(Guid paymentId, PaymentMethod methodName, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var payment = await ctx.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return;
        payment.MethodName = methodName;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeletePaymentAsync(Guid paymentId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var payment = await ctx.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return;
        ctx.Payments.Remove(payment);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RecordedStripePayment>> GetRecordedStripePaymentsAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Payments.AsNoTracking()
            .Where(p => p.StripePaymentIntentId != null)
            .Select(p => new RecordedStripePayment(
                p.StripePaymentIntentId!, p.OrderId, p.AmountEur, p.ReceivedAt, p.Status))
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Invoice>> GetInvoicesForOrdersAsync(
        IReadOnlyCollection<Guid> orderIds,
        CancellationToken ct = default)
    {
        if (orderIds.Count == 0)
            return Array.Empty<Invoice>();

        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.Invoices.AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId))
            .ToListAsync(ct);
    }

    public async Task SaveInvoiceBookingAsync(Invoice invoice, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.Invoices.Attach(invoice);
        var entry = ctx.Entry(invoice);
        entry.Property(i => i.BookingState).IsModified = true;
        entry.Property(i => i.BookingNote).IsModified = true;
        entry.Property(i => i.SentAt).IsModified = true;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveIssuedInvoiceAsync(Invoice invoice, Order order, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.Invoices.Add(invoice);

        // The order is detached from a read taken before several Holded round trips, so its Payments may be stale
        // (a webhook may have settled one). Update(order) would write them back with no concurrency token to catch
        // it, so mark only the columns issuance owns.
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

    public async Task<StoreSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == StoreSettings.SingletonId, ct)
            ?? new StoreSettings();
    }

    public async Task SaveSettingsAsync(StoreSettings settings, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.Settings.FirstOrDefaultAsync(s => s.Id == StoreSettings.SingletonId, ct);
        if (row is null)
            ctx.Settings.Add(row = new StoreSettings());
        row.StripeGatewayTreasuryId = settings.StripeGatewayTreasuryId;
        row.SimplifiedInvoiceThresholdEur = settings.SimplifiedInvoiceThresholdEur;
        await ctx.SaveChangesAsync(ct);
    }
}
