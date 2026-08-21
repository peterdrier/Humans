using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal sealed class VendorCommitmentMatchCandidateConfiguration
    : IEntityTypeConfiguration<VendorCommitmentMatchCandidate>
{
    public void Configure(EntityTypeBuilder<VendorCommitmentMatchCandidate> b)
    {
        b.ToTable("vendor_commitment_match_candidates");
        b.HasKey(x => x.Id);

        b.Property(x => x.HoldedDocId).HasMaxLength(64).IsRequired();
        b.Property(x => x.HoldedDocNumber).HasMaxLength(64).IsRequired();
        b.Property(x => x.ContactName).HasMaxLength(200).IsRequired();
        b.Property(x => x.DocTotal).HasColumnType("decimal(12,2)");

        b.Property(x => x.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // One row per (commitment, document): re-running the matcher refreshes the queue instead
        // of stacking a second copy of the same unresolved decision.
        b.HasIndex(x => new { x.VendorCommitmentId, x.HoldedDocId }).IsUnique();
        b.HasIndex(x => x.ResolvedAt);
    }
}
