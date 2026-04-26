using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class CampRoleDefinitionConfiguration : IEntityTypeConfiguration<CampRoleDefinition>
{
    // Deterministic seed instant — 2026-04-26 00:00:00 UTC. Embedded in the
    // generated AddCampRoles migration via HasData(), so the value is fixed
    // forever. Do not change.
    private static readonly Instant SeedAt = Instant.FromUnixTimeTicks(17463600000000000L);

    // Stable, deterministic IDs for the five seeded role definitions. These
    // values are baked into the AddCampRoles migration and any data referencing
    // them. Do not change.
    private static readonly Guid ConsentLeadId = Guid.Parse("11111111-aaaa-4000-8000-000000000001");
    private static readonly Guid LntId = Guid.Parse("11111111-aaaa-4000-8000-000000000002");
    private static readonly Guid ShitNinjaId = Guid.Parse("11111111-aaaa-4000-8000-000000000003");
    private static readonly Guid PowerId = Guid.Parse("11111111-aaaa-4000-8000-000000000004");
    private static readonly Guid BuildLeadId = Guid.Parse("11111111-aaaa-4000-8000-000000000005");

    public void Configure(EntityTypeBuilder<CampRoleDefinition> builder)
    {
        builder.ToTable("camp_role_definitions");

        builder.Property(d => d.Name).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(2000);

        builder.HasIndex(d => d.Name)
            .IsUnique()
            .HasDatabaseName("IX_camp_role_definitions_name_unique");

        builder.HasIndex(d => d.SortOrder);

        builder.Ignore(d => d.IsActive);

        // Seed the five baseline role definitions. EF Core scaffolds these as
        // InsertData/DeleteData rows in the AddCampRoles migration; do not
        // hand-edit the migration to add or remove rows here — change this seed
        // and add a new migration instead.
        builder.HasData(
            new CampRoleDefinition
            {
                Id = ConsentLeadId,
                Name = "Consent Lead",
                Description = null,
                SlotCount = 2,
                MinimumRequired = 1,
                SortOrder = 10,
                IsRequired = true,
                CreatedAt = SeedAt,
                UpdatedAt = SeedAt,
                DeactivatedAt = null
            },
            new CampRoleDefinition
            {
                Id = LntId,
                Name = "LNT",
                Description = null,
                SlotCount = 1,
                MinimumRequired = 1,
                SortOrder = 20,
                IsRequired = true,
                CreatedAt = SeedAt,
                UpdatedAt = SeedAt,
                DeactivatedAt = null
            },
            new CampRoleDefinition
            {
                Id = ShitNinjaId,
                Name = "Shit Ninja",
                Description = null,
                SlotCount = 1,
                MinimumRequired = 1,
                SortOrder = 30,
                IsRequired = true,
                CreatedAt = SeedAt,
                UpdatedAt = SeedAt,
                DeactivatedAt = null
            },
            new CampRoleDefinition
            {
                Id = PowerId,
                Name = "Power",
                Description = null,
                SlotCount = 1,
                MinimumRequired = 0,
                SortOrder = 40,
                IsRequired = false,
                CreatedAt = SeedAt,
                UpdatedAt = SeedAt,
                DeactivatedAt = null
            },
            new CampRoleDefinition
            {
                Id = BuildLeadId,
                Name = "Build Lead",
                Description = null,
                SlotCount = 2,
                MinimumRequired = 1,
                SortOrder = 50,
                IsRequired = true,
                CreatedAt = SeedAt,
                UpdatedAt = SeedAt,
                DeactivatedAt = null
            });
    }
}
