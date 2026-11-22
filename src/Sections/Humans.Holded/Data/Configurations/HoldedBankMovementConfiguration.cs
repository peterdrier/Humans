using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Holded.Data.Configurations;

internal sealed class HoldedBankMovementConfiguration : IEntityTypeConfiguration<HoldedBankMovement>
{
    public void Configure(EntityTypeBuilder<HoldedBankMovement> b)
    {
        b.ToTable("holded_bank_movements");
        b.HasKey(x => new { x.TreasuryAccountId, x.Id });
        b.Property(x => x.TreasuryAccountId).HasMaxLength(64);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.Amount).HasColumnType("decimal(14,2)");
        b.HasIndex(x => new { x.TreasuryAccountId, x.Date });
    }
}
