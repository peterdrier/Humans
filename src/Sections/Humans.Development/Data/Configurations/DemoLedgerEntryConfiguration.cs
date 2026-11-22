using Humans.Development.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Development.Data.Configurations;

internal sealed class DemoLedgerEntryConfiguration : IEntityTypeConfiguration<DemoLedgerEntry>
{
    public void Configure(EntityTypeBuilder<DemoLedgerEntry> b)
    {
        b.ToTable("demo_ledger");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Key).IsRequired().HasMaxLength(200);
        b.Property(e => e.Generation).IsRequired();
        b.Property(e => e.EntityId).IsRequired();
        b.Property(e => e.State).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Note).HasMaxLength(500);
        b.Property(e => e.CreatedAt).IsRequired();
        b.Property(e => e.UpdatedAt).IsRequired();
        b.HasIndex(e => e.Generation);
    }
}
