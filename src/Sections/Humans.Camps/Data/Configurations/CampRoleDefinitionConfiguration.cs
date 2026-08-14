using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Camps.Data.Configurations;

internal sealed class CampRoleDefinitionConfiguration : IEntityTypeConfiguration<CampRoleDefinition>
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

        // SpecialRole stored as string per the Camps enum convention. No DB
        // default: the one-time backfill it existed for (the AddColumn in
        // 20260519173900_AddSpecialRoleToCampRoleDefinition) is long done, and
        // every insert goes through EF, which always supplies the value. A
        // DEFAULT whose value equals the CLR default (CampSpecialRole.None = 0)
        // also gives EF no sentinel to distinguish "explicitly None" from
        // "unset", which it warns about at every startup.
        builder.Property(d => d.SpecialRole)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Ignore(d => d.IsActive);
    }
}
