using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class GoogleDrivePermissionFailureConfiguration : IEntityTypeConfiguration<GoogleDrivePermissionFailure>
{
    public void Configure(EntityTypeBuilder<GoogleDrivePermissionFailure> builder)
    {
        builder.ToTable("google_drive_permission_failures");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.GoogleId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(f => f.PermissionId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(f => f.Email)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(f => f.ErrorMessage)
            .HasMaxLength(2000)
            .IsRequired();

        builder.Property(f => f.DetectedAt)
            .IsRequired();

        // One terminal-failure row per (Drive file, permission) pair.
        builder.HasIndex(f => new { f.GoogleId, f.PermissionId })
            .IsUnique();
    }
}
