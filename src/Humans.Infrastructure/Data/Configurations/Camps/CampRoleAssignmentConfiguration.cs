using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class CampRoleAssignmentConfiguration : IEntityTypeConfiguration<CampRoleAssignment>
{
    public void Configure(EntityTypeBuilder<CampRoleAssignment> builder)
    {
        builder.ToTable("camp_role_assignments");

        builder.HasOne(a => a.CampSeason)
            .WithMany()
            .HasForeignKey(a => a.CampSeasonId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Definition)
            .WithMany(d => d.Assignments)
            .HasForeignKey(a => a.CampRoleDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.CampMember)
            .WithMany()
            .HasForeignKey(a => a.CampMemberId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.CampSeasonId, a.CampRoleDefinitionId, a.CampMemberId })
            .IsUnique()
            .HasDatabaseName("IX_camp_role_assignments_unique");

        builder.HasIndex(a => a.CampMemberId);
    }
}
