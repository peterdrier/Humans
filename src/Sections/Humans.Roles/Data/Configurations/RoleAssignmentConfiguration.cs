using Humans.Roles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Roles.Data.Configurations;

internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("role_assignments");

        builder.HasKey(a => a.Id);

        builder.HasOne(a => a.Role)
            .WithMany(r => r.Assignments)
            .HasForeignKey(a => a.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.RoleId, a.Slot }).IsUnique();
        builder.HasIndex(a => new { a.RoleId, a.UserId }).IsUnique();
        builder.HasIndex(a => a.UserId);
    }
}
