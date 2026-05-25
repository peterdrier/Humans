using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Profiles;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class CommunicationPreferenceConfiguration : IEntityTypeConfiguration<CommunicationPreference>
{
    public void Configure(EntityTypeBuilder<CommunicationPreference> builder)
    {
        builder.ToTable("communication_preferences");

        builder.HasKey(cp => cp.Id);

        builder.Property(cp => cp.Category)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(cp => cp.OptedOut)
            .IsRequired();

        builder.Property(cp => cp.InboxEnabled)
            .IsRequired()
            .HasDefaultValue(true)
            .HasSentinel(true);

        builder.Property(cp => cp.UpdatedAt)
            .IsRequired();

        builder.Property(cp => cp.UpdateSource)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.SubscribedAt)
            .HasColumnName("SubscribedAt")
            .HasColumnType("timestamp with time zone");

        // Issue #635 (§15i): inverse-side FK preservation after the User-side
        // nav (User.CommunicationPreferences) was stripped.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(cp => cp.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One preference per user per category
        builder.HasIndex(cp => new { cp.UserId, cp.Category })
            .IsUnique();

        builder.HasIndex(cp => cp.UserId);
    }
}
