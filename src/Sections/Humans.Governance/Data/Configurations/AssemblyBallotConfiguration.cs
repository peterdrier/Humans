using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class AssemblyBallotConfiguration : IEntityTypeConfiguration<AssemblyBallot>
{
    public void Configure(EntityTypeBuilder<AssemblyBallot> builder)
    {
        builder.ToTable("assembly_ballots");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Choice)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        GovernanceJson.Ranking(builder, b => b.Ranking);

        builder.Property(b => b.Revision).IsRequired();
        builder.Property(b => b.CastAt).IsRequired();
        builder.Property(b => b.UpdatedAt).IsRequired();

        builder.HasOne(b => b.Vote)
            .WithMany()
            .HasForeignKey(b => b.VoteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(b => b.Roster)
            .WithMany()
            .HasForeignKey(b => b.RosterId)
            .OnDelete(DeleteBehavior.Cascade);

        // One ballot per roster row — the "one person, one vote" invariant, at the schema.
        builder.HasIndex(b => new { b.VoteId, b.RosterId }).IsUnique();
    }
}
