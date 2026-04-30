using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StorePaymentConfiguration : IEntityTypeConfiguration<StorePayment>
{
    public void Configure(EntityTypeBuilder<StorePayment> b)
    {
        b.ToTable("store_payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.AmountEur).HasColumnType("numeric(12,2)");
        b.Property(x => x.Method).HasConversion<int>();
        b.Property(x => x.StripePaymentIntentId).HasMaxLength(200);
        b.Property(x => x.ExternalRef).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.StripePaymentIntentId).IsUnique().HasFilter("\"StripePaymentIntentId\" IS NOT NULL");
    }
}
