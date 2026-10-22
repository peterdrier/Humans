using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class SepaPayoutTransferConfiguration : IEntityTypeConfiguration<SepaPayoutTransfer>
{
    public void Configure(EntityTypeBuilder<SepaPayoutTransfer> b)
    {
        b.ToTable("sepa_payout_transfers");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.FileId);
        b.HasIndex(x => x.UserId);
        b.Property(x => x.HoldedContactId).HasMaxLength(64);
        b.Property(x => x.CreditorName).HasMaxLength(70);
        b.Property(x => x.Iban).HasMaxLength(34);
        b.Property(x => x.IbanMasked).HasMaxLength(34);
        b.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        b.Property(x => x.HoldedBankMovementId).HasMaxLength(64);
        // Unbounded: one 24-char id per paid document overflowed 512 at about 20, and a save failing after Holded took
        // the money is the state this column exists to prevent.
        b.Property(x => x.HoldedPaymentRefs);
    }
}
