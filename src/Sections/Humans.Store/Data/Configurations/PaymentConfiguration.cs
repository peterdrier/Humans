using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Store.Data.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("store_payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.AmountEur).HasColumnType("numeric(12,2)");
        b.Property(x => x.Method).HasConversion<int>();
        b.Property(x => x.MethodName)
            .HasConversion<string>()
            .HasMaxLength(50);
        b.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(50);
        b.Property(x => x.StripePaymentIntentId).HasMaxLength(200);
        b.Property(x => x.ExternalRef).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.StripePaymentIntentId).IsUnique().HasFilter("\"StripePaymentIntentId\" IS NOT NULL");
    }
}
