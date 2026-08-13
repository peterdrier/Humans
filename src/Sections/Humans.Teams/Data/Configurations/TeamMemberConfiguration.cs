using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Teams.Domain;

namespace Humans.Teams.Data.Configurations;

internal sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
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

        // UserId is a bare cross-section Guid column — no FK constraint, no nav.

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
