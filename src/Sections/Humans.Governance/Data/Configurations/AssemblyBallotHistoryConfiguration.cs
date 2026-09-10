using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class AssemblyBallotHistoryConfiguration : IEntityTypeConfiguration<AssemblyBallotHistory>
{
    public void Configure(EntityTypeBuilder<AssemblyBallotHistory> builder)
    {
        builder.ToTable("assembly_ballot_history");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.Revision).IsRequired();

        builder.Property(h => h.Choice)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        GovernanceJson.Ranking(builder, h => h.Ranking);

        builder.Property(h => h.RecordedAt).IsRequired();

        builder.HasOne(h => h.Ballot)
            .WithMany(b => b.History)
            .HasForeignKey(h => h.BallotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(h => new { h.BallotId, h.Revision }).IsUnique();
    }
}
