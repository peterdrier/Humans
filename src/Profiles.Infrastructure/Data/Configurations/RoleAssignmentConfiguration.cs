using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Profiles.Domain.Entities;

namespace Profiles.Infrastructure.Data.Configurations;

public class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("role_assignments");

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

        builder.HasOne(ra => ra.CreatedByUser)
            .WithMany()
            .HasForeignKey(ra => ra.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ra => ra.UserId);
        builder.HasIndex(ra => ra.RoleName);
        builder.HasIndex(ra => new { ra.UserId, ra.RoleName, ra.ValidFrom });
    }
}
