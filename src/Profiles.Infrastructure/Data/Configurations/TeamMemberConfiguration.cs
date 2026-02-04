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
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(tm => tm.JoinedAt)
            .IsRequired();

        builder.HasIndex(tm => new { tm.TeamId, tm.UserId });
        builder.HasIndex(tm => tm.UserId);

        // Ignore computed property
        builder.Ignore(tm => tm.IsActive);
    }
}
