using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class FinanceSettingsConfiguration : IEntityTypeConfiguration<FinanceSettings>
{
    public void Configure(EntityTypeBuilder<FinanceSettings> b)
    {
        b.ToTable("finance_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PleoHoldedContactId).HasMaxLength(64);
        b.Property(x => x.PleoHoldedTreasuryId).HasMaxLength(64);
        // No seed: the row is created on first save.
    }
}

internal sealed class PleoBookingClaimConfiguration : IEntityTypeConfiguration<PleoBookingClaim>
{
    public void Configure(EntityTypeBuilder<PleoBookingClaim> b)
    {
        b.ToTable("finance_pleo_booking_claims");
        b.HasKey(x => x.PleoEntryId);
        b.Property(x => x.PleoEntryId).HasMaxLength(64);
    }
}

internal sealed class PleoReceiptUploadConfiguration : IEntityTypeConfiguration<PleoReceiptUpload>
{
    public void Configure(EntityTypeBuilder<PleoReceiptUpload> b)
    {
        b.ToTable("finance_pleo_receipt_uploads");
        b.HasKey(x => new { x.PleoEntryId, x.ReceiptId });
        b.Property(x => x.PleoEntryId).HasMaxLength(64);
        b.Property(x => x.ReceiptId).HasMaxLength(64);
    }
}
