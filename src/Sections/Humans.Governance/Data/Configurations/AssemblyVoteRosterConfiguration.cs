using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class AssemblyVoteRosterConfiguration : IEntityTypeConfiguration<AssemblyVoteRoster>
{
    public void Configure(EntityTypeBuilder<AssemblyVoteRoster> builder)
    {
        builder.ToTable("assembly_vote_roster");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Tier)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(r => r.IsBoardMember).IsRequired();
        builder.Property(r => r.IsOfficial).IsRequired();

        builder.HasOne(r => r.Vote)
            .WithMany()
            .HasForeignKey(r => r.VoteId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserId is a bare cross-section Guid column — no FK constraint, no nav. Nullable
        // because Art. 17 erasure nulls it and leaves the row as a counting tombstone.
        // The uniqueness rule (one row per person per vote) therefore has to skip the
        // tombstones, which is what the filter is for.
        builder.HasIndex(r => new { r.VoteId, r.UserId })
            .IsUnique()
            .HasFilter("\"UserId\" IS NOT NULL");

        builder.HasIndex(r => r.VoteId);
    }
}
