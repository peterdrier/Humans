using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace Humans.Infrastructure.Data.Configurations;

public class CampRoleDefinitionConfiguration : IEntityTypeConfiguration<CampRoleDefinition>
{
    private static readonly Instant SeedTimestamp = Instant.FromUtc(2026, 4, 24, 0, 0, 0);

    public void Configure(EntityTypeBuilder<CampRoleDefinition> builder)
    {
        builder.ToTable("camp_role_definitions");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(2000);

        builder.Property(d => d.SlotCount).IsRequired();
        builder.Property(d => d.MinimumRequired).IsRequired();
        builder.Property(d => d.SortOrder).IsRequired();
        builder.Property(d => d.IsRequired).IsRequired();
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();
        builder.Property(d => d.DeactivatedAt);

        builder.HasIndex(d => d.Name)
            .IsUnique()
            .HasDatabaseName("IX_camp_role_definitions_name_unique");

        // Seed the 6 global role definitions per spec.
        builder.HasData(
            new { Id = Guid.Parse("c0a0a0a0-0000-0000-0000-000000000001"), Name = "Consent Lead", Description = (string?)null, SlotCount = 2, MinimumRequired = 1, SortOrder = 10, IsRequired = true, CreatedAt = SeedTimestamp, UpdatedAt = SeedTimestamp, DeactivatedAt = (Instant?)null },
            new { Id = Guid.Parse("c0a0a0a0-0000-0000-0000-000000000002"), Name = "Wellbeing Lead", Description = (string?)null, SlotCount = 1, MinimumRequired = 1, SortOrder = 20, IsRequired = true, CreatedAt = SeedTimestamp, UpdatedAt = SeedTimestamp, DeactivatedAt = (Instant?)null },
            new { Id = Guid.Parse("c0a0a0a0-0000-0000-0000-000000000003"), Name = "LNT", Description = (string?)null, SlotCount = 1, MinimumRequired = 1, SortOrder = 30, IsRequired = true, CreatedAt = SeedTimestamp, UpdatedAt = SeedTimestamp, DeactivatedAt = (Instant?)null },
            new { Id = Guid.Parse("c0a0a0a0-0000-0000-0000-000000000004"), Name = "Shit Ninja", Description = (string?)null, SlotCount = 1, MinimumRequired = 1, SortOrder = 40, IsRequired = true, CreatedAt = SeedTimestamp, UpdatedAt = SeedTimestamp, DeactivatedAt = (Instant?)null },
            new { Id = Guid.Parse("c0a0a0a0-0000-0000-0000-000000000005"), Name = "Power", Description = (string?)null, SlotCount = 1, MinimumRequired = 0, SortOrder = 50, IsRequired = false, CreatedAt = SeedTimestamp, UpdatedAt = SeedTimestamp, DeactivatedAt = (Instant?)null },
            new { Id = Guid.Parse("c0a0a0a0-0000-0000-0000-000000000006"), Name = "Build Lead", Description = (string?)null, SlotCount = 2, MinimumRequired = 1, SortOrder = 60, IsRequired = true, CreatedAt = SeedTimestamp, UpdatedAt = SeedTimestamp, DeactivatedAt = (Instant?)null }
        );
    }
}
