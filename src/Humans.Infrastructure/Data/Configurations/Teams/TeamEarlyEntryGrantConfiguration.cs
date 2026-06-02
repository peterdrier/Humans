using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Teams;

public class TeamEarlyEntryGrantConfiguration : IEntityTypeConfiguration<TeamEarlyEntryGrant>
{
    public void Configure(EntityTypeBuilder<TeamEarlyEntryGrant> builder)
    {
        builder.ToTable("team_early_entry_grants");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.ProjectName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(g => g.EntryDate).IsRequired();   // LocalDate via Npgsql NodaTime
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt);                // nullable Instant

        // Same-section FK to Team only — grants die with the team.
        builder.HasOne(g => g.Team)
            .WithMany(t => t.EarlyEntryGrants)
            .HasForeignKey(g => g.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        // NO navigation to User (cross-section). Bare Guid resolved via IUserServiceRead.
        builder.HasIndex(g => g.TeamId);
        builder.HasIndex(g => g.UserId);
    }
}
