using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreOrderLineConfiguration : IEntityTypeConfiguration<StoreOrderLine>
{
    public void Configure(EntityTypeBuilder<StoreOrderLine> b)
    {
        b.ToTable("store_order_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.UnitPriceSnapshot).HasColumnType("numeric(12,2)");
        b.Property(x => x.VatRateSnapshot).HasColumnType("numeric(5,2)");
        b.Property(x => x.DepositAmountSnapshot).HasColumnType("numeric(12,2)");
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.ProductId);
    }
}
