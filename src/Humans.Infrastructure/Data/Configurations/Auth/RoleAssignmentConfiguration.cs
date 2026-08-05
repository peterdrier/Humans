using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Auth;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("role_assignments", table =>
        {
            table.HasCheckConstraint(
                "CK_role_assignments_valid_window",
                "\"ValidTo\" IS NULL OR \"ValidTo\" > \"ValidFrom\"");
        });

        builder.HasKey(ra => ra.Id);

        builder.Property(ra => ra.RoleName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(ra => ra.Notes)
            .HasMaxLength(2000);

        builder.Property(ra => ra.ValidFrom)
            .IsRequired();

        builder.Property(ra => ra.CreatedAt)
            .IsRequired();

        // Cross-section FK columns only — no nav property (design-rules §6c,
        // memory/architecture/no-cross-section-ef-joins.md).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(ra => ra.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Issue #635 (§15i): inverse-side FK preservation after the User-side
        // nav (User.RoleAssignments) was stripped. Configures the schema-level
        // FK + cascade-delete that previously lived on UserConfiguration.HasMany.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(ra => ra.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ra => ra.UserId);
        builder.HasIndex(ra => ra.RoleName);
        builder.HasIndex(ra => new { ra.UserId, ra.RoleName, ra.ValidFrom });

        // Partial index for active role assignments (no end date)
        builder.HasIndex(ra => new { ra.UserId, ra.RoleName })
            .HasFilter("\"ValidTo\" IS NULL");
    }
}
