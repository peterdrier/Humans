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
        b.Property(x => x.CreditorName).HasMaxLength(70);
        b.Property(x => x.Iban).HasMaxLength(34);
        b.Property(x => x.IbanMasked).HasMaxLength(34);
        b.Property(x => x.Amount).HasColumnType("numeric(12,2)");
    }
}
