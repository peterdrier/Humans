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

        // SpecialRole stored as string per the Camps enum convention. The
        // SQL-level default ('None') backfills the AddColumn migration so
        // existing rows land on the enum's None member, not the empty string
        // EF would otherwise generate. SQL-level default (not
        // HasDefaultValue(CampSpecialRole.None)) avoids the EF sentinel trap
        // documented in code-review-rules.md "EF Core Bool Sentinel" —
        // explicit None on a CLR instance still serializes correctly because
        // EF doesn't treat the SQL default as the unset sentinel here.
        builder.Property(d => d.SpecialRole)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired()
            .HasDefaultValueSql("'None'");

        builder.Ignore(d => d.IsActive);
    }
}
