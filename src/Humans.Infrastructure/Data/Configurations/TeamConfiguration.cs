using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    // Fixed seed timestamp so migrations are deterministic
    private static readonly Instant SeedTimestamp = Instant.FromUtc(2026, 2, 4, 23, 52, 37);

    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(t => t.Description)
            .HasMaxLength(2000);

        builder.Property(t => t.Slug)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(t => t.RequiresApproval)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(t => t.SystemTeamType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.Property(t => t.UpdatedAt)
            .IsRequired();

        builder.HasMany(t => t.Members)
            .WithOne(tm => tm.Team)
            .HasForeignKey(tm => tm.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.JoinRequests)
            .WithOne(jr => jr.Team)
            .HasForeignKey(jr => jr.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.GoogleResources)
            .WithOne(gr => gr.Team)
            .HasForeignKey(gr => gr.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(t => t.Slug)
            .IsUnique();

        builder.HasIndex(t => t.IsActive);

        builder.HasIndex(t => t.SystemTeamType);

        // Ignore computed property
        builder.Ignore(t => t.IsSystemTeam);

        // Seed system teams
        builder.HasData(
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000001"),
                Name = "Volunteers",
                Description = "All active volunteers with signed required documents",
                Slug = "volunteers",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Volunteers,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000002"),
                Name = "Leads",
                Description = "All team leads",
                Slug = "leads",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Leads,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000003"),
                Name = "Board",
                Description = "Board members with active role assignments",
                Slug = "board",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Board,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000004"),
                Name = "Asociados",
                Description = "Voting members with approved asociado applications",
                Slug = "asociados",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Asociados,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000005"),
                Name = "Colaboradors",
                Description = "Active contributors with approved colaborador applications",
                Slug = "colaboradors",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Colaboradors,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp
            });
    }
}
