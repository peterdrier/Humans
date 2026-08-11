using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class BoardVoteConfiguration : IEntityTypeConfiguration<BoardVote>
{
    public void Configure(EntityTypeBuilder<BoardVote> builder)
    {
        builder.ToTable("board_votes");

        builder.HasKey(bv => bv.Id);

        builder.Property(bv => bv.Vote)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(bv => bv.Note)
            .HasMaxLength(4000);

        builder.Property(bv => bv.VotedAt)
            .IsRequired();

        builder.HasOne(bv => bv.Application)
            .WithMany(a => a.BoardVotes)
            .HasForeignKey(bv => bv.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // BoardMemberUserId is a bare cross-section Guid column — no FK constraint, no nav.

        // One vote per Board member per application
        builder.HasIndex(bv => new { bv.ApplicationId, bv.BoardMemberUserId })
            .IsUnique();

        builder.HasIndex(bv => bv.ApplicationId);
    }
}
