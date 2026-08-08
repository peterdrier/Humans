using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Store;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Store section
/// (nobodies-collective/Humans#858): maps only <c>store_products</c>,
/// <c>store_orders</c>, <c>store_order_lines</c>, <c>store_payments</c>,
/// <c>store_invoices</c> and <c>store_treasury_sync_state</c>, with its own
/// <c>__EFMigrationsHistory_Store</c> table and migrations under
/// <c>Migrations/Store/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// </remarks>
internal sealed class StoreDbContext(DbContextOptions<StoreDbContext> options)
    : DbContext(options)
{
    public DbSet<StoreProduct> StoreProducts => Set<StoreProduct>();
    public DbSet<StoreOrder> StoreOrders => Set<StoreOrder>();
    public DbSet<StoreOrderLine> StoreOrderLines => Set<StoreOrderLine>();
    public DbSet<StorePayment> StorePayments => Set<StorePayment>();
    public DbSet<StoreInvoice> StoreInvoices => Set<StoreInvoice>();
    public DbSet<StoreTreasurySyncState> StoreTreasurySyncStates => Set<StoreTreasurySyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new StoreProductConfiguration());
        builder.ApplyConfiguration(new StoreOrderConfiguration());
        builder.ApplyConfiguration(new StoreOrderLineConfiguration());
        builder.ApplyConfiguration(new StorePaymentConfiguration());
        builder.ApplyConfiguration(new StoreInvoiceConfiguration());
        builder.ApplyConfiguration(new StoreTreasurySyncStateConfiguration());
    }
}
