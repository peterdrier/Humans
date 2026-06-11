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
        // Stored as string. The column default served the AddStorePaymentStatus migration
        // (existing pre-async rows landed on Paid without a data backfill); EF-side it was the
        // enum-zero sentinel trap (HasDefaultValue on the CLR default), so it was dropped after
        // that migration ran — the entity's C# initializer covers inserts.
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
