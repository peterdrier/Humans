using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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

        builder.HasIndex(tm => new { tm.TeamId, tm.UserId });
        builder.HasIndex(tm => tm.UserId);
        builder.HasIndex(tm => tm.Role);

        // One active membership per team and user.
        builder.HasIndex(tm => new { tm.TeamId, tm.UserId })
            .HasFilter("\"LeftAt\" IS NULL")
            .IsUnique()
            .HasDatabaseName("IX_team_members_active_unique");

        builder.Ignore(tm => tm.IsActive);
    }
}
