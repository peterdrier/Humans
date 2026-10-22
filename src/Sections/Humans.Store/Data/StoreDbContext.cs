using Humans.Store.Data.Configurations;
using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Store.Data;

internal sealed class StoreDbContext(DbContextOptions<StoreDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ProductConfiguration());
        builder.ApplyConfiguration(new OrderConfiguration());
        builder.ApplyConfiguration(new OrderLineConfiguration());
        builder.ApplyConfiguration(new PaymentConfiguration());
        builder.ApplyConfiguration(new InvoiceConfiguration());
    }
}
