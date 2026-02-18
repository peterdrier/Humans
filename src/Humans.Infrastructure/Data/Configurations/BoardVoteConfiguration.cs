using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class BoardVoteConfiguration : IEntityTypeConfiguration<BoardVote>
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

        builder.HasOne(bv => bv.BoardMemberUser)
            .WithMany()
            .HasForeignKey(bv => bv.BoardMemberUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // One vote per Board member per application
        builder.HasIndex(bv => new { bv.ApplicationId, bv.BoardMemberUserId })
            .IsUnique();

        builder.HasIndex(bv => bv.ApplicationId);
    }
}
