using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Teams;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class TeamRoleAssignmentConfiguration : IEntityTypeConfiguration<TeamRoleAssignment>
{
    public void Configure(EntityTypeBuilder<TeamRoleAssignment> builder)
    {
        builder.ToTable("team_role_assignments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.SlotIndex)
            .IsRequired();

        builder.Property(a => a.AssignedAt)
            .IsRequired();

        builder.HasIndex(a => new { a.TeamRoleDefinitionId, a.TeamMemberId })
            .IsUnique()
            .HasDatabaseName("IX_team_role_assignments_definition_member_unique");

        builder.HasIndex(a => new { a.TeamRoleDefinitionId, a.SlotIndex })
            .IsUnique()
            .HasDatabaseName("IX_team_role_assignments_definition_slot_unique");

        builder.HasIndex(a => a.TeamMemberId);

        builder.HasOne(a => a.TeamRoleDefinition)
            .WithMany(d => d.Assignments)
            .HasForeignKey(a => a.TeamRoleDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.TeamMember)
            .WithMany(m => m.RoleAssignments)
            .HasForeignKey(a => a.TeamMemberId)
            .OnDelete(DeleteBehavior.Restrict);

#pragma warning disable CS0618 // Obsolete cross-domain nav kept so EF FK constraint stays modelled.
        builder.HasOne(a => a.AssignedByUser)
            .WithMany()
            .HasForeignKey(a => a.AssignedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
#pragma warning restore CS0618
    }
}
