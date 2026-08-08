using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Teams;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("team_members");

        builder.HasKey(tm => tm.Id);

        builder.Property(tm => tm.Role)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(100);

        builder.Property(tm => tm.JoinedAt)
            .IsRequired();

        // Cross-section FK to Users — bare-FK form, no navigation property.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(tm => tm.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(tm => new { tm.TeamId, tm.UserId });
        builder.HasIndex(tm => tm.UserId);
        builder.HasIndex(tm => tm.Role);

        // Filtered unique index: one active membership per (Team, User)
        builder.HasIndex(tm => new { tm.TeamId, tm.UserId })
            .HasFilter("\"LeftAt\" IS NULL")
            .IsUnique()
            .HasDatabaseName("IX_team_members_active_unique");

        // Ignore computed property
        builder.Ignore(tm => tm.IsActive);
    }
}
