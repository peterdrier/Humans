using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Holded.Data.Configurations;

internal sealed class HoldedAccountConfiguration : IEntityTypeConfiguration<HoldedAccount>
{
    public void Configure(EntityTypeBuilder<HoldedAccount> b)
    {
        b.ToTable("holded_accounts");
        // The chart number is the identity; Holded's own id is just another attribute of it.
        b.HasKey(x => x.Number);
        b.Property(x => x.Number).ValueGeneratedNever();
        b.Property(x => x.HoldedId).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.Group).HasMaxLength(128);
        // Wider than the ledger lines': these are whole-account running totals.
        b.Property(x => x.Debit).HasColumnType("decimal(14,2)");
        b.Property(x => x.Credit).HasColumnType("decimal(14,2)");
        b.Property(x => x.Balance).HasColumnType("decimal(14,2)");
    }
}
