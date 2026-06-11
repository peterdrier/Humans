using Humans.Domain.Entities;
using Humans.Domain.Enums;
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
        // Stored as string; default Paid means existing rows (all pre-date async support and are,
        // by construction, settled) land on Paid at column-add time without a data backfill, and a
        // forgotten Status on a sync/manual insert still records as Paid. See nobodies-collective/Humans#638.
        b.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(StorePaymentStatus.Paid);
        b.Property(x => x.StripePaymentIntentId).HasMaxLength(200);
        b.Property(x => x.ExternalRef).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.StripePaymentIntentId).IsUnique().HasFilter("\"StripePaymentIntentId\" IS NOT NULL");
    }
}
