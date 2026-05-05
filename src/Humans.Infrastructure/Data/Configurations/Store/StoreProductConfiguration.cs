using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreProductConfiguration : IEntityTypeConfiguration<StoreProduct>
{
    public void Configure(EntityTypeBuilder<StoreProduct> b)
    {
        b.ToTable("store_products");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000).IsRequired();
        b.Property(x => x.UnitPriceEur).HasColumnType("numeric(12,2)");
        b.Property(x => x.VatRatePercent).HasColumnType("numeric(5,2)");
        b.Property(x => x.DepositAmountEur).HasColumnType("numeric(12,2)");
        b.HasIndex(x => new { x.Year, x.IsActive });
    }
}
