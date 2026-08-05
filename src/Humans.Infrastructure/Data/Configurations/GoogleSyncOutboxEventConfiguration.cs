using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-08-05",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
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

        builder.Property(e => e.FailedPermanently)
            .IsRequired();

        builder.HasIndex(e => new { e.ProcessedAt, e.OccurredAt });
        builder.HasIndex(e => new { e.TeamId, e.UserId, e.ProcessedAt });
        builder.HasIndex(e => e.DeduplicationKey)
            .IsUnique();
    }
}
