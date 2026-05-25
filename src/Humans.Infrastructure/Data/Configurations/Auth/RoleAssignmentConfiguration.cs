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

        // EF needs the nav ref to configure the cross-section FK relationship.
        // The nav itself is [Obsolete] for Application callers; this block
        // owns the DB-level FK + cascade behavior.
#pragma warning disable CS0618
        builder.HasOne(ra => ra.CreatedByUser)
            .WithMany()
            .HasForeignKey(ra => ra.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
#pragma warning restore CS0618

        // Issue #635 (§15i): inverse-side FK preservation after the User-side
        // nav (User.RoleAssignments) was stripped. Configures the schema-level
        // FK + cascade-delete that previously lived on UserConfiguration.HasMany.
#pragma warning disable CS0618 // RoleAssignment.User is Obsolete; kept for EF FK + inverse nav.
        builder.HasOne(ra => ra.User)
            .WithMany()
            .HasForeignKey(ra => ra.UserId)
            .OnDelete(DeleteBehavior.Cascade);
#pragma warning restore CS0618

        builder.HasIndex(ra => ra.UserId);
        builder.HasIndex(ra => ra.RoleName);
        builder.HasIndex(ra => new { ra.UserId, ra.RoleName, ra.ValidFrom });

        // Partial index for active role assignments (no end date)
        builder.HasIndex(ra => new { ra.UserId, ra.RoleName })
            .HasFilter("\"ValidTo\" IS NULL");
    }
}
