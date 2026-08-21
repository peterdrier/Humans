using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal sealed class VendorCommitmentConfiguration : IEntityTypeConfiguration<VendorCommitment>
{
    public void Configure(EntityTypeBuilder<VendorCommitment> b)
    {
        b.ToTable("vendor_commitments");
        b.HasKey(x => x.Id);

        b.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ExpectedAmount).HasColumnType("decimal(12,2)");
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Purpose).HasMaxLength(500).IsRequired();
        b.Property(x => x.MatchedHoldedDocId).HasMaxLength(64);
        b.Property(x => x.MatchedHoldedDocNumber).HasMaxLength(64);

        b.Property(x => x.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        b.Property(x => x.QuoteFileName).HasMaxLength(255);
        b.Property(x => x.QuoteContentType).HasMaxLength(128);
        b.Property(x => x.QuoteExtension).HasMaxLength(8);

        b.HasMany(x => x.Payments)
            .WithOne(p => p.Commitment!)
            .HasForeignKey(p => p.VendorCommitmentId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.MatchCandidates)
            .WithOne(c => c.Commitment!)
            .HasForeignKey(c => c.VendorCommitmentId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.MatchedHoldedDocId);
        // FK-only ref (no nav) — Budget's key.
        b.HasIndex(x => x.BudgetCategoryId);
    }
}
