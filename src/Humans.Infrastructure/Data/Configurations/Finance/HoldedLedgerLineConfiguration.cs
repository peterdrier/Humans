using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedLedgerLineConfiguration : IEntityTypeConfiguration<HoldedLedgerLine>
{
    public void Configure(EntityTypeBuilder<HoldedLedgerLine> b)
    {
        b.ToTable("holded_ledger_lines");
        b.HasKey(x => x.Id);
        // Natural key for idempotent upsert; journal lines are immutable facts.
        b.HasIndex(x => new { x.EntryNumber, x.Line }).IsUnique();
        b.HasIndex(x => x.AccountNum);
        b.Property(x => x.Type).HasMaxLength(32);
        // Description is left unbounded (text): the cache must never fail an upsert on a long line label.
        b.Property(x => x.Debit).HasColumnType("decimal(12,2)");
        b.Property(x => x.Credit).HasColumnType("decimal(12,2)");
    }
}
