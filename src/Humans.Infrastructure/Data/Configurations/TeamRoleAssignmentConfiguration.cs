using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

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

        builder.HasOne(a => a.AssignedByUser)
            .WithMany()
            .HasForeignKey(a => a.AssignedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
