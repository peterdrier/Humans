using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class GoogleSyncOutboxEventConfiguration : IEntityTypeConfiguration<GoogleSyncOutboxEvent>
{
    public void Configure(EntityTypeBuilder<GoogleSyncOutboxEvent> builder)
    {
        builder.ToTable("google_sync_outbox");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.DeduplicationKey)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.LastError)
            .HasMaxLength(4000);

        builder.Property(e => e.RetryCount)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(e => e.OccurredAt)
            .IsRequired();

        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(e => e.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.ProcessedAt, e.OccurredAt });
        builder.HasIndex(e => new { e.TeamId, e.UserId, e.ProcessedAt });
        builder.HasIndex(e => e.DeduplicationKey)
            .IsUnique();
    }
}
