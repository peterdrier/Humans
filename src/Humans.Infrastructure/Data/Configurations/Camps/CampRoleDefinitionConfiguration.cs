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

        // Slug uniqueness is enforced in C# (DefinitionSlugExistsAsync). Empty
        // slug ("") is a valid state — admin-controlled, set via the role-edit
        // form when the role needs a Google Group. Multiple rows with empty
        // Slug coexist; that's why the DB-level unique index isn't applied.

        builder.HasIndex(d => d.SortOrder);

        builder.Ignore(d => d.IsActive);
    }
}
