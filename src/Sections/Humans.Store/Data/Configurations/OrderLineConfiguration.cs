using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Store.Data.Configurations;

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.ToTable("store_order_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.UnitPriceSnapshot).HasColumnType("numeric(12,2)");
        b.Property(x => x.VatRateSnapshot).HasColumnType("numeric(5,2)");
        b.Property(x => x.DepositAmountSnapshot).HasColumnType("numeric(12,2)");
        b.HasIndex(x => x.OrderId);

        // Intra-section FK to Product — typed-FK form, no navigation property.
        b.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
