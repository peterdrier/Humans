using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampRoleAssignmentConfiguration : IEntityTypeConfiguration<CampRoleAssignment>
{
    public void Configure(EntityTypeBuilder<CampRoleAssignment> builder)
    {
        builder.ToTable("camp_role_assignments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.SlotIndex).IsRequired();
        builder.Property(a => a.AssignedAt).IsRequired();
        builder.Property(a => a.AssignedByUserId).IsRequired();

        // Slot uniqueness: at most one assignment per (season, role, slot).
        builder.HasIndex(a => new { a.CampSeasonId, a.CampRoleDefinitionId, a.SlotIndex })
            .IsUnique()
            .HasDatabaseName("IX_camp_role_assignments_season_role_slot_unique");

        // Within-role uniqueness: same human can't hold two slots of one role per season.
        builder.HasIndex(a => new { a.CampSeasonId, a.CampRoleDefinitionId, a.CampMemberId })
            .IsUnique()
            .HasDatabaseName("IX_camp_role_assignments_season_role_member_unique");

        // Compliance report join helper.
        builder.HasIndex(a => a.CampRoleDefinitionId);

        builder.HasOne(a => a.CampRoleDefinition)
            .WithMany(d => d.Assignments)
            .HasForeignKey(a => a.CampRoleDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.CampSeason)
            .WithMany(s => s.RoleAssignments)
            .HasForeignKey(a => a.CampSeasonId)
            .OnDelete(DeleteBehavior.Restrict);

        // Cascade for hard-delete cases (rare; service-layer cascades on Status=Removed are the primary path).
        builder.HasOne(a => a.CampMember)
            .WithMany(m => m.RoleAssignments)
            .HasForeignKey(a => a.CampMemberId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
