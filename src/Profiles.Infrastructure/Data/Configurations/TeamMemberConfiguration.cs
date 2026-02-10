using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Profiles.Domain.Entities;

namespace Profiles.Infrastructure.Data.Configurations;

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

        builder.HasOne(tm => tm.User)
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
