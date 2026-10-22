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

        // UserId is nullable because Art. 17 erasure nulls it and keeps the row as a counting
        // tombstone; the filter lets the one-row-per-person rule skip those tombstones.
        builder.HasIndex(r => new { r.VoteId, r.UserId })
            .IsUnique()
            .HasFilter("\"UserId\" IS NOT NULL");

        builder.HasIndex(r => r.VoteId);
    }
}
