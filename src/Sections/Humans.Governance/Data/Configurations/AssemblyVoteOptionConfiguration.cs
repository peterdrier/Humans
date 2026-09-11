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

        // No unique index on Key: it is Board-editable display-adjacent text, and
        // memory/architecture/unique-constraints-ids-only.md puts row identity on Id columns
        // only. Duplicate keys within a vote are rejected by IsDraftValid, which is where a
        // duplicate becomes a validation message instead of a constraint violation.
    }
}
