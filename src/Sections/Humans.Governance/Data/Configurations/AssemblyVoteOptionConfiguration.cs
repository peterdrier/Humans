using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class AssemblyVoteOptionConfiguration : IEntityTypeConfiguration<AssemblyVoteOption>
{
    public void Configure(EntityTypeBuilder<AssemblyVoteOption> builder)
    {
        builder.ToTable("assembly_vote_options");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Order).IsRequired();

        builder.Property(o => o.Key)
            .IsRequired()
            .HasMaxLength(100);

        GovernanceJson.LocalizedText(builder, o => o.Label);

        builder.HasOne(o => o.Vote)
            .WithMany(v => v.Options)
            .HasForeignKey(o => o.VoteId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ballots store the option key, so it must be unique within the vote.
        builder.HasIndex(o => new { o.VoteId, o.Key }).IsUnique();
    }
}
