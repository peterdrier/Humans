using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class TeamRoleDefinitionConfiguration : IEntityTypeConfiguration<TeamRoleDefinition>
{
    public void Configure(EntityTypeBuilder<TeamRoleDefinition> builder)
    {
        builder.ToTable("team_role_definitions");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(d => d.Description)
            .HasMaxLength(2000);

        builder.Property(d => d.SlotCount)
            .IsRequired();

        builder.Property(d => d.Priorities)
            .IsRequired()
            .HasMaxLength(500)
            .HasConversion(
                v => string.Join(",", v.Select(p => p.ToString())),
                v => string.IsNullOrEmpty(v)
                    ? new List<SlotPriority>()
                    : v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => Enum.Parse<SlotPriority>(s))
                        .ToList())
            .HasDefaultValueSql("''");

        builder.Property(d => d.SortOrder)
            .IsRequired();

        builder.Property(d => d.CreatedAt)
            .IsRequired();

        builder.Property(d => d.UpdatedAt)
            .IsRequired();

        // Case-insensitive unique index — will be replaced with lower() in migration
        builder.HasIndex(d => new { d.TeamId, d.Name })
            .IsUnique()
            .HasDatabaseName("IX_team_role_definitions_team_name_unique");

        builder.HasIndex(d => d.TeamId);

        builder.HasOne(d => d.Team)
            .WithMany(t => t.RoleDefinitions)
            .HasForeignKey(d => d.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(d => d.Period)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.IsManagement)
            .IsRequired();

    }
}
