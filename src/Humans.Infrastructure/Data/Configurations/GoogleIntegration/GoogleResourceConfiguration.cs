using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.GoogleIntegration;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class GoogleResourceConfiguration : IEntityTypeConfiguration<GoogleResource>
{
    public void Configure(EntityTypeBuilder<GoogleResource> builder)
    {
        builder.ToTable("google_resources");

        builder.HasKey(gr => gr.Id);

        builder.Property(gr => gr.ResourceType)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(gr => gr.GoogleId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(gr => gr.Name)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(gr => gr.Url)
            .HasMaxLength(2048);

        builder.Property(gr => gr.ProvisionedAt)
            .IsRequired();

        builder.Property(gr => gr.ErrorMessage)
            .HasMaxLength(2000);

        builder.Property(gr => gr.DrivePermissionLevel)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.HasIndex(gr => gr.GoogleId);

        builder.HasIndex(gr => gr.TeamId);
        builder.HasIndex(gr => gr.IsActive);

        // Filtered unique index: one active resource per (Team, GoogleId)
        builder.HasIndex(gr => new { gr.TeamId, gr.GoogleId })
            .HasFilter("\"IsActive\" = true")
            .IsUnique();

        // Restrict (not SetNull): GoogleResource.TeamId is non-nullable, so
        // SetNull would produce a NOT NULL violation on team delete. Teams
        // should never be hard-deleted if resources exist; callers must unlink
        // resources first.
        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(gr => gr.TeamId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
