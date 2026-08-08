using Humans.Store.Data.Configurations;
using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Store.Data;

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
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<TreasurySyncState> TreasurySyncStates => Set<TreasurySyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ProductConfiguration());
        builder.ApplyConfiguration(new OrderConfiguration());
        builder.ApplyConfiguration(new OrderLineConfiguration());
        builder.ApplyConfiguration(new PaymentConfiguration());
        builder.ApplyConfiguration(new InvoiceConfiguration());
        builder.ApplyConfiguration(new TreasurySyncStateConfiguration());
    }
}
