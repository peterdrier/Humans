using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class CampRoleDefinitionConfiguration : IEntityTypeConfiguration<CampRoleDefinition>
{
    public void Configure(EntityTypeBuilder<CampRoleDefinition> builder)
    {
        builder.ToTable("camp_role_definitions");

        builder.Property(d => d.Name).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(2000);
        builder.Property(d => d.Slug).HasMaxLength(60).IsRequired();

        builder.HasIndex(d => d.Name)
            .IsUnique()
            .HasDatabaseName("IX_camp_role_definitions_name_unique");

        // Slug is normalized to lower-case kebab at service entry, so a plain
        // unique index suffices for case-insensitive uniqueness.
        builder.HasIndex(d => d.Slug)
            .IsUnique()
            .HasDatabaseName("IX_camp_role_definitions_slug_unique");

        builder.HasIndex(d => d.SortOrder);

        builder.Ignore(d => d.IsActive);
    }
}
