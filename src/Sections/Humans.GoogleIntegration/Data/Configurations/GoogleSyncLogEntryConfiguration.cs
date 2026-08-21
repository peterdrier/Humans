using Humans.GoogleIntegration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.GoogleIntegration.Data.Configurations;

/// <summary>Append-only sync trail; ResourceId and UserId are bare Guid columns — no FK, no nav.</summary>
internal sealed class GoogleSyncLogEntryConfiguration : IEntityTypeConfiguration<GoogleSyncLogEntry>
{
    public void Configure(EntityTypeBuilder<GoogleSyncLogEntry> builder)
    {
        builder.ToTable("google_sync_log");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Action)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(e => e.ResourceId)
            .IsRequired();

        builder.Property(e => e.UserEmail)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(e => e.Role)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.Source)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(e => e.Success)
            .IsRequired();

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(4000);

        builder.Property(e => e.Description)
            .HasMaxLength(4000)
            .IsRequired();

        builder.Property(e => e.JobName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.OccurredAt)
            .IsRequired();

        builder.HasIndex(e => e.ResourceId);
        builder.HasIndex(e => e.UserId);
        builder.HasIndex(e => e.OccurredAt);
    }
}
