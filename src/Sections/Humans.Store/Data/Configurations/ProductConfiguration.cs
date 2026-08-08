using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Store.Data.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
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
