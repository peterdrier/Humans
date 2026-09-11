using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class AssemblyVoteConfiguration : IEntityTypeConfiguration<AssemblyVote>
{
    public void Configure(EntityTypeBuilder<AssemblyVote> builder)
    {
        builder.ToTable("assembly_votes");

        builder.HasKey(v => v.Id);

        GovernanceJson.LocalizedText(builder, v => v.Title);
        GovernanceJson.LocalizedText(builder, v => v.OfficialText);

        builder.Property(v => v.OfficialCulture)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(v => v.InfoUrl)
            .HasMaxLength(2000);

        // Every enum is string-converted: the values are part of the legal record and a
        // CLR reorder must never silently reinterpret a stored vote.
        builder.Property(v => v.Kind).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(v => v.RequiredMajority).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(v => v.IndicativeAudience).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(v => v.BallotDisclosure).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(v => v.Status).IsRequired().HasConversion<string>().HasMaxLength(30);

        builder.Property(v => v.ClosesAt).IsRequired();

        builder.Property(v => v.CancelReason)
            .HasMaxLength(4000);

        builder.Property(v => v.ResultJson)
            .HasColumnType("jsonb");

        builder.Property(v => v.CreatedAt).IsRequired();
        builder.Property(v => v.UpdatedAt).IsRequired();

        // OpenedByUserId / ClosedByUserId / CreatedByUserId are bare cross-section Guid
        // columns — no FK constraint, no nav (design-rules §6).

        builder.HasIndex(v => v.Status);

        // Drives the hourly lapse-and-reminder sweep.
        builder.HasIndex(v => new { v.Status, v.ClosesAt });
    }
}
