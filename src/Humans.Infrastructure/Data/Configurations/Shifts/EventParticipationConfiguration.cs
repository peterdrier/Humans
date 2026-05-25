using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class EventParticipationConfiguration : IEntityTypeConfiguration<EventParticipation>
{
    public void Configure(EntityTypeBuilder<EventParticipation> builder)
    {
        builder.ToTable("event_participations");

        builder.HasKey(ep => ep.Id);

        builder.Property(ep => ep.UserId)
            .IsRequired();

        builder.Property(ep => ep.Year)
            .IsRequired();

        builder.Property(ep => ep.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(ep => ep.Source)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        // Unique constraint on (UserId, Year)
        builder.HasIndex(ep => new { ep.UserId, ep.Year })
            .IsUnique();

        builder.HasOne(ep => ep.User)
            .WithMany(u => u.EventParticipations)
            .HasForeignKey(ep => ep.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
