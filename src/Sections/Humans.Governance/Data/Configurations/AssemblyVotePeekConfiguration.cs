using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class AssemblyVotePeekConfiguration : IEntityTypeConfiguration<AssemblyVotePeek>
{
    public void Configure(EntityTypeBuilder<AssemblyVotePeek> builder)
    {
        builder.ToTable("assembly_vote_peeks");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.PeekedAt).IsRequired();

        builder.HasOne(p => p.Vote)
            .WithMany()
            .HasForeignKey(p => p.VoteId)
            .OnDelete(DeleteBehavior.Cascade);

        // AdminUserId is a bare cross-section Guid column — no FK constraint, no nav.

        builder.HasIndex(p => p.VoteId);
    }
}
